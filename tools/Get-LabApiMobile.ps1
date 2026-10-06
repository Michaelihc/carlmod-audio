<#
.SYNOPSIS
  Puts the LabAPI-Mobile framework (LabApi.dll, 0Harmony.dll) where the build looks for it.

.DESCRIPTION
  Downloads the LabAPI-Mobile release archive from GitHub and extracts it to
  <repo>\.runtime\refs\LabApiMobile-<version>\ (the default LabApiDir of Directory.Build.props points at its
  framework folder). With -Zip, extracts an archive you already have instead of downloading.

.PARAMETER Version  LabAPI-Mobile version (default 1.1.7-mobile.3).
.PARAMETER Zip      Existing LabApiMobile-<version>.zip to extract instead of downloading.
#>
[CmdletBinding()]
param(
    [string]$Version = '1.1.7-mobile.3',
    [string]$Zip
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$refs = Join-Path $repo '.runtime\refs'
$name = "LabApiMobile-$Version"
$target = Join-Path $refs $name
if (Test-Path -LiteralPath (Join-Path $target 'framework\LabApi.dll')) {
    Write-Host "Already present: $target"
    return
}
New-Item -ItemType Directory -Force $refs | Out-Null

if (-not $Zip) {
    $Zip = Join-Path $refs "$name.zip"
    $url = "https://github.com/Michaelihc/labapimobile/releases/download/v$Version/$name.zip"
    Write-Host "Downloading $url"
    Invoke-WebRequest -Uri $url -OutFile $Zip -UseBasicParsing
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$tmp = Join-Path $refs ".extract-$([Guid]::NewGuid().ToString('N'))"
[IO.Compression.ZipFile]::ExtractToDirectory($Zip, $tmp)
try {
    $framework = Get-ChildItem -LiteralPath $tmp -Recurse -Directory -Filter framework | Select-Object -First 1
    if (-not $framework -or -not (Test-Path -LiteralPath (Join-Path $framework.FullName 'LabApi.dll'))) {
        throw "No framework\LabApi.dll in $Zip."
    }
    if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
    Move-Item -LiteralPath $framework.Parent.FullName -Destination $target
} finally {
    if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Recurse -Force }
}
Write-Host "LabAPI-Mobile $Version framework: $(Join-Path $target 'framework')"
