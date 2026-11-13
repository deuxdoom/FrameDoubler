param([string]$DistPath='dist\FrameDoubler',[string]$WorkRoot='F:\Temp\FrameDoubler\package',[string]$PocWorkRoot='F:\Temp\FrameDoubler\ncnn-poc')
$ErrorActionPreference='Stop'
& (Join-Path $PSScriptRoot 'build-native.ps1') -DistPath $DistPath -WorkRoot $WorkRoot -PocWorkRoot $PocWorkRoot
if($LASTEXITCODE -ne 0){throw 'FrameDoubler build failed'}
