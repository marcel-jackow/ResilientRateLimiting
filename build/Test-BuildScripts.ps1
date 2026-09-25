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

# --- end ---

Remove-Item -LiteralPath $root -Recurse -Force
if ($failures.Count -gt 0) {
    throw "$($failures.Count) build script test(s) failed."
}
Write-Host 'All build script tests passed.'
