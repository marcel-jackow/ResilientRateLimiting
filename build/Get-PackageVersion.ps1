# Prints the package version for this CI run: the release tag's version, or <VersionPrefix>-ci.<run number>.
param(
    [string]$EventName = $env:GITHUB_EVENT_NAME,
    [string]$RefName = $env:GITHUB_REF_NAME,
    [string]$RunNumber = $env:GITHUB_RUN_NUMBER,
    [string]$PropsPath = (Join-Path $PSScriptRoot '..' 'Directory.Build.props')
)

$ErrorActionPreference = 'Stop'

[xml]$props = Get-Content -Raw -LiteralPath $PropsPath
$prefixes = @($props.SelectNodes('/Project/PropertyGroup/VersionPrefix') | ForEach-Object { $_.InnerText.Trim() })
if ($prefixes.Count -ne 1) {
    throw "Expected one VersionPrefix in $PropsPath, found $($prefixes.Count)."
}
$prefix = $prefixes[0]

if ($EventName -eq 'release') {
    if ($RefName -notmatch '^v(?<version>(?<core>\d+\.\d+\.\d+)(-[0-9A-Za-z][0-9A-Za-z.-]*)?)$') {
        throw "Release tag '$RefName' must look like v1.2.3 or v1.2.3-rc.1."
    }
    if ($Matches.core -ne $prefix) {
        throw "Release tag '$RefName' is version $($Matches.core), but VersionPrefix in Directory.Build.props is $prefix. Change VersionPrefix in a PR first, or fix the tag."
    }
    $Matches.version
}
else {
    if ($RunNumber -notmatch '^\d+$') {
        throw "GITHUB_RUN_NUMBER '$RunNumber' is not a number."
    }
    "$prefix-ci.$RunNumber"
}
