# Sets up the FrameDoubler development environment.
#   - .venv with PyTorch (CUDA build) and the project in editable mode
#   - FFmpeg (LGPL build) under tools\ffmpeg
# Usage: pwsh scripts\setup.ps1
$ErrorActionPreference = 'Stop'

$Root = Split-Path -Parent $PSScriptRoot
$TempRoot = 'F:\Temp\FrameDoubler'
$TorchIndex = 'https://download.pytorch.org/whl/cu130'
$FfmpegAsset = 'ffmpeg-n9.0-latest-win64-lgpl-9.0.zip'
$FfmpegUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/$FfmpegAsset"

if (Test-Path $TempRoot) { $env:PIP_CACHE_DIR = Join-Path $TempRoot 'pip-cache' }

$Python = Join-Path $Root '.venv\Scripts\python.exe'
if (-not (Test-Path $Python)) {
    python -m venv (Join-Path $Root '.venv')
}
& $Python -m pip install --quiet --upgrade pip
& $Python -m pip install --quiet torch --index-url $TorchIndex
& $Python -m pip install --quiet -e "$Root[dev]"
if ($LASTEXITCODE -ne 0) { throw 'pip install failed' }

$FfmpegExe = Join-Path $Root 'tools\ffmpeg\bin\ffmpeg.exe'
if (-not (Test-Path $FfmpegExe)) {
    $Work = Join-Path $Root 'tools\_download'
    New-Item -ItemType Directory -Force $Work | Out-Null
    $Zip = Join-Path $Work $FfmpegAsset
    Invoke-WebRequest -Uri $FfmpegUrl -OutFile $Zip
    Expand-Archive -LiteralPath $Zip -DestinationPath $Work -Force
    $Extracted = Get-ChildItem $Work -Directory | Select-Object -First 1
    $Target = Join-Path $Root 'tools\ffmpeg'
    if (Test-Path $Target) { Remove-Item -Recurse -Force $Target }
    Move-Item -LiteralPath $Extracted.FullName -Destination $Target
    Remove-Item -Recurse -Force $Work
}

& $FfmpegExe -hide_banner -version | Select-Object -First 1
& $Python -c "import torch; print('torch', torch.__version__, 'cuda', torch.cuda.is_available())"
& $Python -m framedoubler --version
