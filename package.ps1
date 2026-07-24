# Builds the plugin in Release mode and produces dist/trailer-downloader_<version>.zip
# ready to drop into Jellyfin's plugins directory.
$ErrorActionPreference = 'Stop'

$version = '1.1.3.0'
$root = $PSScriptRoot
$proj = Join-Path $root 'Jellyfin.Plugin.TrailerDownloader\Jellyfin.Plugin.TrailerDownloader.csproj'
$outDir = Join-Path $root 'dist\trailer-downloader'
$zipPath = Join-Path $root "dist\trailer-downloader_$version.zip"

dotnet build $proj -c Release
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }

if (Test-Path $outDir) { Remove-Item -Recurse -Force $outDir }
New-Item -ItemType Directory -Force $outDir | Out-Null

$binDir = Join-Path $root 'Jellyfin.Plugin.TrailerDownloader\bin\Release\net9.0'
Copy-Item (Join-Path $binDir 'Jellyfin.Plugin.TrailerDownloader.dll') $outDir
Copy-Item (Join-Path $root 'assets\banner.png') (Join-Path $outDir 'image.png')

$meta = [ordered]@{
    guid        = '8f7fd897-4d3c-4a06-b8b1-8ca9b1c1a042'
    name        = 'Trailer Downloader'
    description = 'Downloads YouTube movie trailers and stores them locally next to your movies.'
    overview    = 'Downloads YouTube movie trailers and stores them locally next to your movies.'
    owner       = 'soldemeyer'
    category    = 'General'
    version     = $version
    changelog   = 'YouTube cookies are now pasted directly into settings instead of requiring a file on the server.'
    targetAbi   = '10.11.0.0'
    framework   = 'net9.0'
    autoUpdate  = $false
    imagePath   = 'image.png'
    status      = 'Active'
    timestamp   = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
}
$meta | ConvertTo-Json | Set-Content (Join-Path $outDir 'meta.json') -Encoding utf8

if (Test-Path $zipPath) { Remove-Item -Force $zipPath }
Compress-Archive -Path (Join-Path $outDir '*') -DestinationPath $zipPath

# Keep the repository manifest in sync with the freshly built zip.
$manifestPath = Join-Path $root 'manifest.json'
if (Test-Path $manifestPath) {
    $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
    $entry = $manifest[0].versions | Where-Object { $_.version -eq $version }
    if ($entry) {
        $entry.checksum = (Get-FileHash $zipPath -Algorithm MD5).Hash.ToLower()
        $entry.timestamp = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
        $manifest | ConvertTo-Json -Depth 5 -AsArray | Set-Content $manifestPath -Encoding utf8
        Write-Host "manifest.json checksum/timestamp updated for version $version"
    }
}

Write-Host "`nPackage created: $zipPath"
Write-Host "To install manually: extract into <jellyfin data dir>\plugins\TrailerDownloader_$version\ and restart Jellyfin."
