# Checks the packed .nupkg/.snupkg files before they can be published.
param(
    [Parameter(Mandatory)][string]$Version,
    [string]$ArtifactsPath = 'artifacts'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$expected = [ordered]@{
    'ResilientRateLimiting'            = @{ Dependencies = @('Polly.Core', 'System.Threading.RateLimiting'); Frameworks = @() }
    'ResilientRateLimiting.AspNetCore' = @{ Dependencies = @('ResilientRateLimiting'); Frameworks = @('Microsoft.AspNetCore.App') }
}
$problems = [System.Collections.Generic.List[string]]::new()

$files = @(Get-ChildItem -LiteralPath $ArtifactsPath -File)
$nupkgs = @($files | Where-Object Extension -eq '.nupkg')
$snupkgs = @($files | Where-Object Extension -eq '.snupkg')
if ($nupkgs.Count -ne 2) { $problems.Add("Expected 2 .nupkg files, found $($nupkgs.Count): $($nupkgs.Name -join ', ')") }
if ($snupkgs.Count -ne 2) { $problems.Add("Expected 2 .snupkg files, found $($snupkgs.Count): $($snupkgs.Name -join ', ')") }

foreach ($id in $expected.Keys) {
    $nupkgPath = Join-Path $ArtifactsPath "$id.$Version.nupkg"
    if (-not (Test-Path -LiteralPath (Join-Path $ArtifactsPath "$id.$Version.snupkg"))) { $problems.Add("Missing $id.$Version.snupkg") }
    if (-not (Test-Path -LiteralPath $nupkgPath)) { $problems.Add("Missing $id.$Version.nupkg"); continue }

    $zip = [System.IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $nupkgPath).ProviderPath)
    try {
        $entries = @($zip.Entries | ForEach-Object FullName)
        $nuspecEntry = $zip.GetEntry("$id.nuspec")
        if ($null -eq $nuspecEntry) { $problems.Add("${id}: no $id.nuspec in the package"); continue }
        $reader = [System.IO.StreamReader]::new($nuspecEntry.Open())
        try { [xml]$nuspec = $reader.ReadToEnd() } finally { $reader.Dispose() }
    }
    finally { $zip.Dispose() }

    $ns = [System.Xml.XmlNamespaceManager]::new($nuspec.NameTable)
    $ns.AddNamespace('n', $nuspec.DocumentElement.NamespaceURI)
    $metadata = $nuspec.SelectSingleNode('/n:package/n:metadata', $ns)

    $versionNode = $metadata.SelectSingleNode('n:version', $ns)
    $packedVersion = if ($null -ne $versionNode) { $versionNode.InnerText } else { '' }
    if ($packedVersion -ne $Version) { $problems.Add("${id}: nuspec version is '$packedVersion', expected '$Version'") }

    $readmeNode = $metadata.SelectSingleNode('n:readme', $ns)
    if ($null -eq $readmeNode -or $readmeNode.InnerText -ne 'README.md' -or $entries -notcontains 'README.md') {
        $problems.Add("${id}: README.md is not packed as the package readme")
    }

    if ($entries -notcontains "lib/net10.0/$id.dll") { $problems.Add("${id}: lib/net10.0/$id.dll is missing") }

    foreach ($group in @($metadata.SelectNodes('n:dependencies/n:group', $ns))) {
        $framework = $group.GetAttribute('targetFramework')
        if ($framework -ne 'net10.0') { $problems.Add("${id}: dependency group for '$framework', expected only net10.0") }
    }

    $dependencies = @($metadata.SelectNodes('n:dependencies/n:group/n:dependency', $ns))
    $dependencyIds = @($dependencies | ForEach-Object { $_.GetAttribute('id') } | Sort-Object)
    $wantedIds = @($expected[$id].Dependencies | Sort-Object)
    if (($dependencyIds -join ',') -ne ($wantedIds -join ',')) {
        $problems.Add("${id}: dependencies are [$($dependencyIds -join ', ')], expected [$($wantedIds -join ', ')]")
    }
    foreach ($dependency in $dependencies) {
        if ($dependency.GetAttribute('id') -eq 'ResilientRateLimiting' -and $dependency.GetAttribute('version') -ne $Version) {
            $problems.Add("${id}: depends on ResilientRateLimiting $($dependency.GetAttribute('version')), expected $Version")
        }
    }

    $frameworks = @($metadata.SelectNodes('n:frameworkReferences/n:group/n:frameworkReference', $ns) | ForEach-Object { $_.GetAttribute('name') } | Sort-Object)
    $wantedFrameworks = @($expected[$id].Frameworks | Sort-Object)
    if (($frameworks -join ',') -ne ($wantedFrameworks -join ',')) {
        $problems.Add("${id}: framework references are [$($frameworks -join ', ')], expected [$($wantedFrameworks -join ', ')]")
    }
}

if ($problems.Count -gt 0) {
    $problems | ForEach-Object { Write-Host "::error::$_" }
    throw "Package check failed with $($problems.Count) problem(s)."
}
Write-Host "Package check passed for version $Version."
