# Local test using the standalone Windows build (no Unity editor involved).
# Starts MediaMTX (hidden) + DroneSim.exe, then offers the bbox viewer.
# NOTE: keep this file ASCII-only (PowerShell 5.1 misparses UTF-8 without BOM).
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $root "Builds\Windows\DroneSim.exe"

if (-not (Test-Path $exe)) {
    Write-Host "Build not found: $exe" -ForegroundColor Red
    Write-Host "Build it first (BuildScript.BuildWindows or ask Claude)."
    exit 1
}

if (-not (Get-Process mediamtx -ErrorAction SilentlyContinue)) {
    Start-Process -FilePath "$PSScriptRoot\mediamtx\mediamtx.exe" -WorkingDirectory "$PSScriptRoot\mediamtx" -WindowStyle Hidden
    Write-Host "MediaMTX started (hidden; RTSP :8554, HLS :8888)." -ForegroundColor Green
} else {
    Write-Host "MediaMTX already running." -ForegroundColor Green
}

if (Get-Process DroneSim -ErrorAction SilentlyContinue) {
    Write-Host "DroneSim already running." -ForegroundColor Green
} else {
    Start-Process -FilePath $exe -ArgumentList "-screen-width", "640", "-screen-height", "360", "-screen-fullscreen", "0"
    Write-Host "DroneSim started (small window - minimizing it is fine, closing it stops the sim)." -ForegroundColor Green
}

Write-Host ""
Write-Host "Quick check: http://127.0.0.1:8888/cam0 in a browser (~15s until first video)." -ForegroundColor Yellow
Read-Host "Press Enter to launch the bbox-overlay viewer (ESC quits it)" | Out-Null
python "$PSScriptRoot\viewer.py"
