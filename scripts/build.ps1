param([string]$DistPath='dist\FrameDoubler',[string]$WorkRoot=(Join-Path (Split-Path -Parent $PSScriptRoot) 'build\package'),[string]$PocWorkRoot=(Join-Path (Split-Path -Parent $PSScriptRoot) 'tools\dev\ncnn'))
$ErrorActionPreference='Stop'
& (Join-Path $PSScriptRoot 'build-native.ps1') -DistPath $DistPath -WorkRoot $WorkRoot -PocWorkRoot $PocWorkRoot
if($LASTEXITCODE -ne 0){throw 'FrameDoubler build failed'}
