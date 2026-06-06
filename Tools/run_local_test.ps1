# Local pipeline self-test: starts MediaMTX, waits for you to press Play in Unity,
# then opens the ground-truth viewer. ESC in the viewer window quits.
# NOTE: keep this file ASCII-only (PowerShell 5.1 misparses UTF-8 without BOM).
$ErrorActionPreference = "Stop"

if (-not (Test-Path "$PSScriptRoot\mediamtx\mediamtx.exe")) {
    Write-Host "mediamtx.exe not found in Tools\mediamtx\ - download it from:" -ForegroundColor Red
    Write-Host "https://github.com/bluenviron/mediamtx/releases  (windows_amd64.zip, unzip there)"
    exit 1
}

if (-not (Get-Process mediamtx -ErrorAction SilentlyContinue)) {
    Start-Process -FilePath "$PSScriptRoot\mediamtx\mediamtx.exe" -WorkingDirectory "$PSScriptRoot\mediamtx" -WindowStyle Minimized
    Write-Host "MediaMTX started (RTSP :8554, HLS preview :8888)." -ForegroundColor Green
} else {
    Write-Host "MediaMTX already running." -ForegroundColor Green
}

Write-Host ""
Write-Host "1. In Unity: open Assets/Scenes/SimScene.unity and press PLAY." -ForegroundColor Yellow
Write-Host "2. Wait ~10s (encoder fallback on this machine takes a moment)."
Write-Host "3. Quick browser check (no Python needed): http://127.0.0.1:8888/cam0"
Write-Host ""
Read-Host "Press Enter to launch the bbox-overlay viewer (or Ctrl+C to skip)" | Out-Null
python "$PSScriptRoot\viewer.py"
