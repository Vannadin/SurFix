# 릴리스 zip 패키징 — GameData 레이아웃(DLL+version+LICENSE+README)으로 묶음
$ErrorActionPreference = 'Stop'

& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'build.ps1')
if ($LASTEXITCODE -ne 0) { throw 'build failed' }

$version = (Get-Content (Join-Path $PSScriptRoot 'GameData\PQSCityPrecisionFix\PQSCityPrecisionFix.version') | ConvertFrom-Json).VERSION
$verStr = "$($version.MAJOR).$($version.MINOR).$($version.PATCH)"

$stage = Join-Path $PSScriptRoot "staging\GameData\PQSCityPrecisionFix"
if (Test-Path (Join-Path $PSScriptRoot 'staging')) { Remove-Item (Join-Path $PSScriptRoot 'staging') -Recurse -Force }
New-Item -ItemType Directory -Force $stage | Out-Null

Copy-Item (Join-Path $PSScriptRoot 'bin\PQSCityPrecisionFix.dll') $stage
Copy-Item (Join-Path $PSScriptRoot 'GameData\PQSCityPrecisionFix\PQSCityPrecisionFix.version') $stage
Copy-Item (Join-Path $PSScriptRoot 'LICENSE') $stage
Copy-Item (Join-Path $PSScriptRoot 'README.md') $stage

$relDir = Join-Path $PSScriptRoot 'releases'
New-Item -ItemType Directory -Force $relDir | Out-Null
$zip = Join-Path $relDir "PQSCityPrecisionFix-$verStr.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $PSScriptRoot 'staging\GameData') -DestinationPath $zip
Remove-Item (Join-Path $PSScriptRoot 'staging') -Recurse -Force

# fail-closed: verify the zip actually contains the dll
Add-Type -AssemblyName System.IO.Compression.FileSystem
$z = [System.IO.Compression.ZipFile]::OpenRead($zip)
$names = $z.Entries | ForEach-Object { $_.FullName -replace '\\', '/' }
$z.Dispose()
if (-not ($names -contains 'GameData/PQSCityPrecisionFix/PQSCityPrecisionFix.dll')) { throw 'zip missing dll' }
Write-Output ("packaged: " + $zip)
$names | ForEach-Object { Write-Output ("  " + $_) }
