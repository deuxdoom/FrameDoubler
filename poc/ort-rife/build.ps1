param([string]$WorkRoot=(Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'tools/dev/onnx'))
$ErrorActionPreference='Stop'
$Root=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$Compiler=Join-Path $Root 'tools/dev/ncnn/compiler/llvm-mingw-20261104-ucrt-x86_64/bin/clang++.exe'
$Include=Join-Path $WorkRoot 'include'
& (Join-Path $PSScriptRoot 'bootstrap.ps1') -WorkRoot $WorkRoot
New-Item -ItemType Directory -Force -Path (Join-Path $WorkRoot 'bin') | Out-Null
$ResourceRoot=if($env:FRAMEDOUBLER_TEMP_ROOT){Join-Path $env:FRAMEDOUBLER_TEMP_ROOT 'ort-build'}else{Join-Path $WorkRoot 'bin'}
New-Item -ItemType Directory -Force -Path $ResourceRoot | Out-Null
$Resource=Join-Path $ResourceRoot 'ort-resource.o'
& (Join-Path (Split-Path -Parent $Compiler) 'x86_64-w64-mingw32-windres.exe') "-I$PSScriptRoot" '-i' (Join-Path $PSScriptRoot 'ort.rc') '-o' $Resource '-O' 'coff'
if($LASTEXITCODE -ne 0){throw 'ORT worker manifest compilation failed'}
& $Compiler '-std=c++17' '-O2' '-static' '-municode' "-I$Include" (Join-Path $PSScriptRoot 'main.cpp') $Resource '-o' (Join-Path $WorkRoot 'bin/rife-ort.exe')
if($LASTEXITCODE -ne 0){throw 'ORT worker build failed'}
