# 릴리스 zip 패키징 — GameData 레이아웃(DLL+version+LICENSE+README)으로 묶음
$ErrorActionPreference = 'Stop'

& (Join-Path $PSScriptRoot 'build.ps1') -NoDeploy

$version = (Get-Content (Join-Path $PSScriptRoot 'GameData\PQSCityPrecisionFix\PQSCityPrecisionFix.version') | ConvertFrom-Json).VERSION
$verStr = "$($version.MAJOR).$($version.MINOR).$($version.PATCH)"

$stagingRoot = Join-Path $PSScriptRoot 'staging'
$stage = Join-Path $stagingRoot 'GameData\PQSCityPrecisionFix'
if (Test-Path $stagingRoot) { Remove-Item $stagingRoot -Recurse -Force }
New-Item -ItemType Directory -Force $stage | Out-Null

Copy-Item (Join-Path $PSScriptRoot 'bin\PQSCityPrecisionFix.dll') $stage
Copy-Item (Join-Path $PSScriptRoot 'GameData\PQSCityPrecisionFix\PQSCityPrecisionFix.version') $stage
Copy-Item (Join-Path $PSScriptRoot 'LICENSE') $stage
Copy-Item (Join-Path $PSScriptRoot 'README.md') $stage

$relDir = Join-Path $PSScriptRoot 'releases'
New-Item -ItemType Directory -Force $relDir | Out-Null
$zip = Join-Path $relDir "PQSCityPrecisionFix-$verStr.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }

# entry names are set explicitly with '/': both Compress-Archive and .NET
# Framework's CreateFromDirectory emit '\', which Linux/macOS unzip treats as
# one flat filename
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::Open($zip, 'Create')
foreach ($f in Get-ChildItem $stagingRoot -Recurse -File) {
    $rel = $f.FullName.Substring($stagingRoot.Length + 1) -replace '\\', '/'
    [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $f.FullName, $rel) | Out-Null
}
$archive.Dispose()
Remove-Item $stagingRoot -Recurse -Force

# fail-closed: verify content and separators
$z = [System.IO.Compression.ZipFile]::OpenRead($zip)
$names = $z.Entries | ForEach-Object { $_.FullName }
$z.Dispose()
if ($names -match '\\') { throw 'zip entries contain backslashes' }
if (-not ($names -contains 'GameData/PQSCityPrecisionFix/PQSCityPrecisionFix.dll')) { throw 'zip missing dll' }
Write-Output ("packaged: " + $zip)
$names | ForEach-Object { Write-Output ("  " + $_) }
