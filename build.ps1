# SurFix 빌드 스크립트 — KSP Managed 참조로 csc 컴파일, 옵션으로 테스트 게임에 배포
param(
    [string]$Ksp = 'F:\project\2026\KK\KSP_KKTest',
    [string]$Csc = 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe',
    [switch]$NoDeploy
)
$ErrorActionPreference = 'Stop'

$managed = Join-Path $Ksp 'KSP_x64_Data\Managed'
if (-not (Test-Path $Csc)) { throw "csc not found: $Csc (pass -Csc <path>)" }
if (-not (Test-Path $managed)) { throw "KSP Managed not found: $managed (pass -Ksp <install>)" }

$src = Join-Path $PSScriptRoot 'src\SurFix.cs'
$outDir = Join-Path $PSScriptRoot 'bin'
New-Item -ItemType Directory -Force $outDir | Out-Null
$out = Join-Path $outDir 'SurFix.dll'

& $Csc -nologo -target:library -optimize+ -deterministic `
    "-out:$out" `
    "-r:$managed\Assembly-CSharp.dll" `
    "-r:$managed\Assembly-CSharp-firstpass.dll" `
    "-r:$managed\UnityEngine.dll" `
    "-r:$managed\UnityEngine.CoreModule.dll" `
    $src
if ($LASTEXITCODE -ne 0) { throw "csc failed with $LASTEXITCODE" }

$dll = Get-Item $out
Write-Output ("built: " + $dll.FullName + " " + $dll.Length + " bytes " + $dll.LastWriteTime.ToString('o'))
if ($NoDeploy) { return }

# deploy; if the game holds the file, pass only when the deployed copy already
# matches the fresh deterministic build (fail-closed on real staleness)
$dest = Join-Path $Ksp 'GameData\SurFix'
New-Item -ItemType Directory -Force $dest | Out-Null
$destFile = Join-Path $dest 'SurFix.dll'
$srcHash = (Get-FileHash $out).Hash
try {
    Copy-Item $out $destFile -Force -ErrorAction Stop
    if ((Get-FileHash $destFile).Hash -ne $srcHash) { throw 'deploy hash mismatch' }
    Write-Output ("deployed: " + $destFile + " (hash verified)")
} catch {
    if ((Test-Path $destFile) -and ((Get-FileHash $destFile).Hash -eq $srcHash)) {
        Write-Output ("deploy target locked (game running) but already up to date: " + $destFile)
    } else {
        throw 'deploy target locked AND stale - close the game and rerun'
    }
}
