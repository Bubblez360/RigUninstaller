<#
  Builds the download for GitHub Releases: a self-contained, single-file RigUninstaller.exe (it brings its own .NET,
  so the person downloading it installs nothing), zipped with the license and a short how-to, plus SHA-256 checksums.

  Usage:   powershell -File tools\make-release.ps1
           powershell -File tools\make-release.ps1 -Version 1.1.0     (default: the Version in RigUninstaller.csproj)
  Output:  dist\RigUninstaller-v<version>-win-x64.zip and dist\SHA256SUMS.txt   (dist\ is not committed)
#>
param([string]$Version = '')

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

if (-not $Version) {
    $xml = [xml](Get-Content (Join-Path $root 'src\RigUninstaller\RigUninstaller.csproj'))
    $Version = @($xml.Project.PropertyGroup.Version | Where-Object { $_ })[0]
}
if (-not $Version) { throw 'No version found. Pass -Version 1.0.0' }

$name = "RigUninstaller-v$Version-win-x64"
$dist = Join-Path $root 'dist'
$build = Join-Path $dist 'build'
$stage = Join-Path $dist $name
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }

# Self-contained: the exe carries the .NET runtime. Compressed, so it is far smaller than the raw runtime.
dotnet publish src\RigUninstaller -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -o $build --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

New-Item -ItemType Directory -Path $stage | Out-Null
Copy-Item (Join-Path $build 'RigUninstaller.exe') $stage
Copy-Item (Join-Path $root 'LICENSE') $stage

@"
RigUninstaller $Version

HOW TO RUN
1. Unzip this folder somewhere normal, for example C:\Tools\RigUninstaller or your Documents folder.
   (Not inside the zip, and not in Program Files: the app saves its logs and backups in a Data folder next to the exe.)
2. Double-click RigUninstaller.exe and answer Yes to the Windows permission prompt.
   It needs administrator rights to run uninstallers and change the registry.
3. Nothing to install. The .NET runtime is already inside the exe. The first start takes a few seconds longer.

IF WINDOWS SAYS "Windows protected your PC" (SmartScreen)
The exe is not code-signed yet, so Windows warns about every new unsigned program.
Click "More info", then "Run anyway". You can check the file first: compare its SHA-256 with the one on the
download page, or upload it to https://www.virustotal.com. The full source code is public.

SAFE TO TRY
It never deletes anything by itself. Every removal asks first, registry keys are exported before they change,
files go to the Recycle Bin, and the Undo center can put things back.
Temp and cache cleaning is the one exception: those files are deleted for good (after a confirmation).

UPDATING
Unzip the new version over the old folder, or into a new one and copy the Data folder across.

Source code, help and bug reports: https://github.com/Bubblez360/RigUninstaller
Like it? https://ko-fi.com/bubzwot
"@ | Set-Content (Join-Path $stage 'README.txt') -Encoding utf8

$zip = Join-Path $dist "$name.zip"
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal

# Checksums of the zip and of the exe inside it, so people can check what they downloaded.
$lines = foreach ($file in @($zip, (Join-Path $stage 'RigUninstaller.exe'))) {
    '{0}  {1}' -f (Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant(), (Split-Path $file -Leaf)
}
$lines | Set-Content (Join-Path $dist 'SHA256SUMS.txt') -Encoding ascii

Write-Host ''
Write-Host "Built $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)"
Get-Content (Join-Path $dist 'SHA256SUMS.txt') | ForEach-Object { Write-Host "  $_" }
