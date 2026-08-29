# PQSCityPrecisionFix 빌드 스크립트 — KSP_KKTest Managed 참조로 csc 컴파일 후 테스트 게임에 배포
$ErrorActionPreference = 'Stop'

$csc = 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe'
$ksp = 'F:\project\2026\KK\KSP_KKTest'
$managed = Join-Path $ksp 'KSP_x64_Data\Managed'
$src = Join-Path $PSScriptRoot 'src\PQSCityPrecisionFix.cs'
$outDir = Join-Path $PSScriptRoot 'bin'
New-Item -ItemType Directory -Force $outDir | Out-Null
$out = Join-Path $outDir 'PQSCityPrecisionFix.dll'

& $csc -nologo -target:library -optimize+ -deterministic `
    "-out:$out" `
    "-r:$managed\Assembly-CSharp.dll" `
    "-r:$managed\Assembly-CSharp-firstpass.dll" `
    "-r:$managed\UnityEngine.dll" `
    "-r:$managed\UnityEngine.CoreModule.dll" `
    $src
if ($LASTEXITCODE -ne 0) { throw "csc failed with $LASTEXITCODE" }

$dll = Get-Item $out
Write-Output ("built: " + $dll.FullName + " " + $dll.Length + " bytes " + $dll.LastWriteTime.ToString('o'))

# deploy into the test install; if the game holds the file, pass only when the
# deployed copy already matches the fresh build (fail-closed on real staleness)
$dest = Join-Path $ksp 'GameData\PQSCityPrecisionFix'
New-Item -ItemType Directory -Force $dest | Out-Null
$destFile = Join-Path $dest 'PQSCityPrecisionFix.dll'
try {
    Copy-Item $out $destFile -Force -ErrorAction Stop
    if ((Get-FileHash $destFile).Hash -ne (Get-FileHash $out).Hash) { throw 'deploy hash mismatch' }
    Write-Output ("deployed: " + $destFile + " (hash verified)")
} catch [System.IO.IOException] {
    if ((Test-Path $destFile) -and ((Get-FileHash $destFile).Hash -eq (Get-FileHash $out).Hash)) {
        Write-Output ("deploy target locked (game running) but already up to date: " + $destFile)
    } else {
        throw 'deploy target locked AND stale - close the game and rerun'
    }
}
