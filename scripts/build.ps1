param([string]$DistPath='dist\FrameDoubler',[string]$WorkRoot=$(if($env:FRAMEDOUBLER_TEMP_ROOT){Join-Path $env:FRAMEDOUBLER_TEMP_ROOT 'package'}else{Join-Path (Split-Path -Parent $PSScriptRoot) 'build\package'}),[string]$PocWorkRoot=(Join-Path (Split-Path -Parent $PSScriptRoot) 'tools\dev\ncnn'),[switch]$SkipAcceleration,[switch]$IncludeNvidiaVfx)
$ErrorActionPreference='Stop'
& (Join-Path $PSScriptRoot 'build-native.ps1') -DistPath $DistPath -WorkRoot $WorkRoot -PocWorkRoot $PocWorkRoot -SkipAcceleration:$SkipAcceleration -IncludeNvidiaVfx:$IncludeNvidiaVfx
if($LASTEXITCODE -ne 0){throw 'FrameDoubler build failed'}
