<#
.SYNOPSIS
  Builds CarlModAudio in Release and writes the release archive dist\CarlModAudio-<version>.zip.

.DESCRIPTION
  The archive has one top folder, CarlModAudio-<version>\, containing:
    plugins\CarlModAudio.dll, .pdb        into LabAPI-Mobile\plugins\global
    dependencies\NVorbis.dll              into LabAPI-Mobile\dependencies\global
    audio\chime.wav                       a short synthesized test sound for the audio folder
    licenses\LICENSE.txt, NVorbis-MIT.txt
    README.md, README.zh-CN.md, NOTICE.md
    INSTALL.txt, INSTALL.zh-CN.txt        from tools\package

  The script fails if CarlModAudio.dll references an assembly that neither the game, LabAPI-Mobile nor the archive
  provides.

.PARAMETER Version      Package version (default: <Version> of src\CarlModAudio\CarlModAudio.csproj).
.PARAMETER OutputDir    Where the zip and the staging folder go (default: <repo>\dist).
.PARAMETER CarlManaged  Carl Mod server Managed folder (default: the MSBuild default).
.PARAMETER LabApiDir    LabAPI-Mobile framework folder (default: the MSBuild default, see tools\Get-LabApiMobile.ps1).
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$OutputDir,
    [string]$CarlManaged,
    [string]$LabApiDir
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'src\CarlModAudio\CarlModAudio.csproj'
if (-not $OutputDir) { $OutputDir = Join-Path $repo 'dist' }
$OutputDir = [IO.Path]::GetFullPath($OutputDir)
$artifacts = Join-Path $OutputDir 'build'
$templates = Join-Path $PSScriptRoot 'package'

$props = @()
if ($CarlManaged) { $props += "-p:CarlManaged=$([IO.Path]::GetFullPath($CarlManaged))" }
if ($LabApiDir) { $props += "-p:LabApiDir=$([IO.Path]::GetFullPath($LabApiDir))" }

if (-not $Version) {
    $Version = ([xml](Get-Content -LiteralPath $project -Raw)).Project.PropertyGroup.Version |
        Where-Object { $_ } | Select-Object -First 1
    if (-not $Version) { throw "No <Version> in $project; pass -Version." }
}

$CarlManaged = (& dotnet msbuild $project -nologo -getProperty:CarlManaged @props).Trim()
$LabApiDir = (& dotnet msbuild $project -nologo -getProperty:LabApiDir @props).Trim()
if (-not (Test-Path -LiteralPath (Join-Path $CarlManaged 'Assembly-CSharp.dll'))) { throw "Carl Mod assemblies not found in $CarlManaged." }
if (-not (Test-Path -LiteralPath (Join-Path $LabApiDir 'LabApi.dll'))) { throw "LabApi.dll not found in $LabApiDir; run tools\Get-LabApiMobile.ps1." }

$commit = (& git -C $repo rev-parse --short HEAD 2>$null)
if (-not $commit) { $commit = 'unknown' }
if (& git -C $repo status --porcelain 2>$null) {
    Write-Warning 'The working tree has uncommitted changes; the package does not match a commit.'
    $commit = "$commit (with uncommitted changes)"
}

Write-Host "Building CarlModAudio (Release) into $artifacts"
& dotnet build $project -c Release --artifacts-path $artifacts -nologo -v quiet @props
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE" }
$bin = Join-Path $artifacts 'bin\CarlModAudio\release'
$dll = Join-Path $bin 'CarlModAudio.dll'
$nvorbis = Join-Path $bin 'NVorbis.dll'
foreach ($f in $dll, $nvorbis) { if (-not (Test-Path -LiteralPath $f)) { throw "Build output missing: $f" } }

# Every reference must come from the game, LabAPI-Mobile or the archive.
$provided = @{ 'LabApi' = $true; '0Harmony' = $true; 'NVorbis' = $true }
Get-ChildItem -LiteralPath $CarlManaged -Filter *.dll | ForEach-Object { $provided[$_.BaseName] = $true }
$refs = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($dll)).GetReferencedAssemblies()
$missing = @($refs | Where-Object { -not $provided.ContainsKey($_.Name) } | ForEach-Object Name)
if ($missing.Count -gt 0) { throw "CarlModAudio.dll references assemblies nobody provides: $($missing -join ', ')" }

# The PDB must not contain local paths.
$pdb = Join-Path $bin 'CarlModAudio.pdb'
$pdbText = [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($pdb))
# Also as escaped in JSON (a Source Link document map), and the user name of the build account.
$escapedRepo = [regex]::Escape($repo.Replace('\', '\\'))
if ($pdbText -match [regex]::Escape($repo) -or $pdbText -match $escapedRepo -or $pdbText -match '[A-Za-z]:(\\){1,2}Users(\\){1,2}' -or
    ($env:USERNAME.Length -ge 4 -and $pdbText -match [regex]::Escape($env:USERNAME))) { throw "$pdb contains a local path or the user name." }

$name = "CarlModAudio-$Version"
$stage = Join-Path $OutputDir $name
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
foreach ($d in 'plugins', 'dependencies', 'audio', 'licenses') { New-Item -ItemType Directory -Force (Join-Path $stage $d) | Out-Null }

Copy-Item -LiteralPath $dll, $pdb -Destination (Join-Path $stage 'plugins')
Copy-Item -LiteralPath $nvorbis -Destination (Join-Path $stage 'dependencies')
Copy-Item -LiteralPath (Join-Path $repo 'samples\chime.wav') -Destination (Join-Path $stage 'audio')
Copy-Item -LiteralPath (Join-Path $repo 'LICENSE') -Destination (Join-Path $stage 'licenses\LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $repo 'licenses\NVorbis-MIT.txt') -Destination (Join-Path $stage 'licenses')
foreach ($doc in 'README.md', 'README.zh-CN.md', 'NOTICE.md') { Copy-Item -LiteralPath (Join-Path $repo $doc) -Destination $stage }

foreach ($install in 'INSTALL.txt', 'INSTALL.zh-CN.txt') {
    $text = [IO.File]::ReadAllText((Join-Path $templates $install))
    $text = $text.Replace('{{VERSION}}', $Version).Replace('{{COMMIT}}', $commit)
    $text = ($text -replace "`r`n", "`n") -replace "`n", "`r`n"
    [IO.File]::WriteAllText((Join-Path $stage $install), $text, (New-Object Text.UTF8Encoding($true)))
}

Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$zipPath = Join-Path $OutputDir "$name.zip"
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
$zip = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Create)
try {
    Get-ChildItem -LiteralPath $stage -Recurse -File | Sort-Object FullName | ForEach-Object {
        $entry = "$name/" + $_.FullName.Substring($stage.Length + 1).Replace('\', '/')
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $_.FullName, $entry, [IO.Compression.CompressionLevel]::Optimal)
    }
} finally { $zip.Dispose() }

Write-Host ''
Write-Host "Package: $zipPath ($([math]::Round((Get-Item -LiteralPath $zipPath).Length / 1KB)) KB)"
Get-ChildItem -LiteralPath $stage -Recurse -File | Sort-Object FullName | ForEach-Object {
    '{0,10:N0}  {1}' -f $_.Length, $_.FullName.Substring($stage.Length + 1)
}
