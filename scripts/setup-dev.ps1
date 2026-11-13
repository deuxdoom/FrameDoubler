param([string]$WorkRoot='F:\Temp\FrameDoubler')
$ErrorActionPreference='Stop'
$env:TEMP=Join-Path $WorkRoot 'build-temp';$env:TMP=$env:TEMP
$env:PIP_CACHE_DIR=Join-Path $WorkRoot 'pip-cache'
New-Item -ItemType Directory -Force -Path $env:TEMP | Out-Null
$Environment=Join-Path $WorkRoot 'build-env'
$Python=Join-Path $Environment 'Scripts\python.exe'
if(-not(Test-Path -LiteralPath $Python)){& python -m venv $Environment;if($LASTEXITCODE -ne 0){throw 'Python build environment creation failed'}}
& $Python -m pip install --disable-pip-version-check 'cmake==4.4.4' 'ninja==1.13.2'
if($LASTEXITCODE -ne 0){throw 'Build tools installation failed'}
& dotnet --version
if($LASTEXITCODE -ne 0){throw 'Install the .NET SDK specified in global.json'}
Write-Output "Build tools ready: $Environment (no Torch/Qt). FFmpeg and converted models remain external build inputs."
