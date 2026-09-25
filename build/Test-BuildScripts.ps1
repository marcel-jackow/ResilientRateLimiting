# Tests for the scripts in this folder. CI runs it in the pack job.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$root = Join-Path ([System.IO.Path]::GetTempPath()) "rrl-build-tests-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $root | Out-Null
$failures = [System.Collections.Generic.List[string]]::new()

function Invoke-Script([string]$script, [string[]]$arguments) {
    $ErrorActionPreference = 'Continue'
    $output = & pwsh -NoProfile -File (Join-Path $PSScriptRoot $script) @arguments 2>&1
    [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = ($output | Out-String).Trim() }
}

function Test-Case([string]$name, [scriptblock]$check) {
    try {
        & $check
        Write-Host "PASS $name"
    }
    catch {
        $failures.Add("${name}: $_")
        Write-Host "FAIL ${name}: $_"
    }
}

function Assert-Passes($result) {
    if ($result.ExitCode -ne 0) { throw "expected success, got exit $($result.ExitCode): $($result.Output)" }
}

function Assert-Fails($result, [string]$expectedText) {
    if ($result.ExitCode -eq 0) { throw "expected failure, got success: $($result.Output)" }
    if (-not $result.Output.Contains($expectedText)) { throw "expected output to contain '$expectedText', got: $($result.Output)" }
}

function Assert-Output($result, [string]$expected) {
    Assert-Passes $result
    if ($result.Output -ne $expected) { throw "expected output '$expected', got '$($result.Output)'" }
}

# --- Get-PackageVersion.ps1 ---

$props = Join-Path $root 'Directory.Build.props'
Set-Content -LiteralPath $props -Value '<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><PropertyGroup Condition="x"><VersionPrefix>0.1.0</VersionPrefix></PropertyGroup></Project>'
$noPrefixProps = Join-Path $root 'NoPrefix.props'
Set-Content -LiteralPath $noPrefixProps -Value '<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>'

function Get-Version([string]$event, [string]$ref, [string]$run, [string]$propsPath = $props) {
    Invoke-Script 'Get-PackageVersion.ps1' @('-EventName', $event, '-RefName', $ref, '-RunNumber', $run, '-PropsPath', $propsPath)
}

Test-Case 'release tag equal to VersionPrefix gives that version' {
    Assert-Output (Get-Version 'release' 'v0.1.0' '5') '0.1.0'
}
Test-Case 'release tag with a pre-release suffix keeps the suffix' {
    Assert-Output (Get-Version 'release' 'v0.1.0-rc.1' '5') '0.1.0-rc.1'
}
Test-Case 'release tag different from VersionPrefix fails' {
    Assert-Fails (Get-Version 'release' 'v0.1.1' '5') 'VersionPrefix'
}
Test-Case 'release tag without the v fails' {
    Assert-Fails (Get-Version 'release' '0.1.0' '5') 'must look like'
}
Test-Case 'release tag with two version parts fails' {
    Assert-Fails (Get-Version 'release' 'v0.1' '5') 'must look like'
}
Test-Case 'push to main gives a ci version' {
    Assert-Output (Get-Version 'push' 'main' '42') '0.1.0-ci.42'
}
Test-Case 'pull request gives a ci version' {
    Assert-Output (Get-Version 'pull_request' '7/merge' '7') '0.1.0-ci.7'
}
Test-Case 'a run number that is not a number fails' {
    Assert-Fails (Get-Version 'push' 'main' 'abc') 'GITHUB_RUN_NUMBER'
}
Test-Case 'props without VersionPrefix fails' {
    Assert-Fails (Get-Version 'push' 'main' '1' $noPrefixProps) 'VersionPrefix'
}
Test-Case 'default props path reads the real Directory.Build.props' {
    $result = Invoke-Script 'Get-PackageVersion.ps1' @('-EventName', 'push', '-RefName', 'main', '-RunNumber', '1')
    Assert-Passes $result
    if ($result.Output -notmatch '^\d+\.\d+\.\d+-ci\.1$') { throw "unexpected version '$($result.Output)'" }
}

# --- Test-Packages.ps1 ---

function Add-Entry($zip, [string]$name, [string]$content) {
    $entry = $zip.CreateEntry($name)
    $writer = [System.IO.StreamWriter]::new($entry.Open())
    try { $writer.Write($content) } finally { $writer.Dispose() }
}

function New-FakePackage {
    param(
        [string]$Dir,
        [string]$Id,
        [string]$Version,
        [string[]]$Dependencies = @(),
        [string]$DependencyVersion = $Version,
        [string[]]$FrameworkReferences = @(),
        [switch]$NoReadme,
        [switch]$NoSymbols,
        [switch]$NoDll,
        [switch]$NoNuspec,
        [string]$NuspecVersion = $Version,
        [string]$DependencyFramework = 'net10.0'
    )
    $deps = ($Dependencies | ForEach-Object { "<dependency id=""$_"" version=""$DependencyVersion"" exclude=""Build,Analyzers"" />" }) -join ''
    $frameworks = ($FrameworkReferences | ForEach-Object { "<frameworkReference name=""$_"" />" }) -join ''
    $readme = if ($NoReadme) { '' } else { '<readme>README.md</readme>' }
    $nuspec = @"
<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
  <metadata>
    <id>$Id</id>
    <version>$NuspecVersion</version>
    $readme
    <dependencies><group targetFramework="$DependencyFramework">$deps</group></dependencies>
    <frameworkReferences><group targetFramework="net10.0">$frameworks</group></frameworkReferences>
  </metadata>
</package>
"@
    $zip = [System.IO.Compression.ZipFile]::Open((Join-Path $Dir "$Id.$Version.nupkg"), 'Create')
    try {
        if (-not $NoNuspec) { Add-Entry $zip "$Id.nuspec" $nuspec }
        if (-not $NoDll) { Add-Entry $zip "lib/net10.0/$Id.dll" 'fake' }
        if (-not $NoReadme) { Add-Entry $zip 'README.md' '# fake' }
    }
    finally { $zip.Dispose() }
    if (-not $NoSymbols) { Set-Content -LiteralPath (Join-Path $Dir "$Id.$Version.snupkg") -Value 'fake' }
}

# Builds a valid pair of packages; the switches break one thing each.
function New-PackageSet {
    param(
        [string]$Name,
        [string]$Version = '0.1.0-ci.1',
        [switch]$CoreExtraDependency,
        [switch]$AspNetCoreWrongCoreVersion,
        [switch]$AspNetCoreNoFramework,
        [switch]$CoreNoSymbols,
        [switch]$CoreNoReadme,
        [switch]$CoreNoDll,
        [switch]$CoreNoNuspec,
        [string]$CoreNuspecVersion = $Version,
        [string]$CoreDependencyFramework = 'net10.0',
        [switch]$ThirdPackage
    )
    $dir = Join-Path $root $Name
    New-Item -ItemType Directory -Path $dir | Out-Null
    $coreDeps = @('Polly.Core', 'System.Threading.RateLimiting')
    if ($CoreExtraDependency) { $coreDeps += 'StackExchange.Redis' }
    New-FakePackage -Dir $dir -Id 'ResilientRateLimiting' -Version $Version -Dependencies $coreDeps -NoSymbols:$CoreNoSymbols -NoReadme:$CoreNoReadme -NoDll:$CoreNoDll -NoNuspec:$CoreNoNuspec -NuspecVersion $CoreNuspecVersion -DependencyFramework $CoreDependencyFramework
    $coreVersion = if ($AspNetCoreWrongCoreVersion) { '0.0.9' } else { $Version }
    $frameworks = if ($AspNetCoreNoFramework) { @() } else { @('Microsoft.AspNetCore.App') }
    New-FakePackage -Dir $dir -Id 'ResilientRateLimiting.AspNetCore' -Version $Version -Dependencies @('ResilientRateLimiting') -DependencyVersion $coreVersion -FrameworkReferences $frameworks
    if ($ThirdPackage) { New-FakePackage -Dir $dir -Id 'ResilientRateLimiting.Extra' -Version $Version }
    $dir
}

function Test-Packages([string]$dir, [string]$version = '0.1.0-ci.1') {
    Invoke-Script 'Test-Packages.ps1' @('-Version', $version, '-ArtifactsPath', $dir)
}

Test-Case 'a valid package pair passes' {
    Assert-Passes (Test-Packages (New-PackageSet 'valid'))
}
Test-Case 'an extra core dependency fails' {
    Assert-Fails (Test-Packages (New-PackageSet 'extra-dep' -CoreExtraDependency)) 'StackExchange.Redis'
}
Test-Case 'AspNetCore depending on another core version fails' {
    Assert-Fails (Test-Packages (New-PackageSet 'wrong-core-version' -AspNetCoreWrongCoreVersion)) '0.0.9'
}
Test-Case 'AspNetCore without the ASP.NET Core framework reference fails' {
    Assert-Fails (Test-Packages (New-PackageSet 'no-framework' -AspNetCoreNoFramework)) 'Microsoft.AspNetCore.App'
}
Test-Case 'a missing symbol package fails' {
    Assert-Fails (Test-Packages (New-PackageSet 'no-symbols' -CoreNoSymbols)) 'ResilientRateLimiting.0.1.0-ci.1.snupkg'
}
Test-Case 'a package without README fails' {
    Assert-Fails (Test-Packages (New-PackageSet 'no-readme' -CoreNoReadme)) 'README.md'
}
Test-Case 'a third package fails' {
    Assert-Fails (Test-Packages (New-PackageSet 'third' -ThirdPackage)) 'Expected 2 .nupkg'
}
Test-Case 'packages with another version than asked fail' {
    Assert-Fails (Test-Packages (New-PackageSet 'other-version') '0.1.0-ci.2') 'Missing ResilientRateLimiting.0.1.0-ci.2.nupkg'
}
Test-Case 'a nuspec version different from the file name fails' {
    Assert-Fails (Test-Packages (New-PackageSet 'nuspec-version' -CoreNuspecVersion '0.0.1')) "nuspec version is '0.0.1'"
}
Test-Case 'a package without its dll fails' {
    Assert-Fails (Test-Packages (New-PackageSet 'no-dll' -CoreNoDll)) 'lib/net10.0/ResilientRateLimiting.dll is missing'
}
Test-Case 'a dependency group for another framework fails' {
    Assert-Fails (Test-Packages (New-PackageSet 'other-framework' -CoreDependencyFramework 'net8.0')) "dependency group for 'net8.0'"
}
Test-Case 'a package without its nuspec fails' {
    Assert-Fails (Test-Packages (New-PackageSet 'no-nuspec' -CoreNoNuspec)) 'no ResilientRateLimiting.nuspec in the package'
}

# --- end ---

Remove-Item -LiteralPath $root -Recurse -Force
if ($failures.Count -gt 0) {
    throw "$($failures.Count) build script test(s) failed."
}
Write-Host 'All build script tests passed.'
# The failure cases leave a non-zero LASTEXITCODE behind; CI's pwsh step exits with it.
exit 0
