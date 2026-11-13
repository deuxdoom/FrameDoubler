param(
    [string]$WorkRoot = 'F:\Temp\FrameDoubler\ncnn-poc'
)
$ErrorActionPreference = 'Stop'
$TaskTemp = Join-Path $WorkRoot 'tmp'
New-Item -ItemType Directory -Force -Path $TaskTemp | Out-Null
$env:TEMP = $TaskTemp
$env:TMP = $TaskTemp
$CompilerBin = Join-Path $WorkRoot 'compiler\llvm-mingw-20261104-ucrt-x86_64\bin'
$ProjectRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$Cmake = Join-Path $ProjectRoot '.venv\Scripts\cmake.exe'
$env:PATH = "$CompilerBin;$(Join-Path $ProjectRoot '.venv\Scripts');$env:PATH"
$Build = Join-Path $WorkRoot 'build-native'
$Source = Join-Path $WorkRoot 'native\ncnn-20261020'
$Destination = Join-Path $WorkRoot 'deploy-vulkan'
New-Item -ItemType Directory -Force -Path $Destination | Out-Null
& $Cmake -S $PSScriptRoot -B $Build -G Ninja "-DNCNN_SOURCE_DIR=$Source" '-DCMAKE_C_COMPILER=clang.exe' '-DCMAKE_CXX_COMPILER=clang++.exe' '-DCMAKE_BUILD_TYPE=Release' '-DNCNN_VULKAN=ON' '-DNCNN_SIMPLEVK=ON' '-DNCNN_SHARED_LIB=OFF' '-DNCNN_OPENMP=OFF' '-DNCNN_BUILD_TOOLS=OFF' '-DNCNN_BUILD_EXAMPLES=OFF' '-DNCNN_BUILD_BENCHMARK=OFF' '-DNCNN_AVX512=OFF' '-DNCNN_AVX=OFF' '-DNCNN_AVX2=OFF' '-DNCNN_FMA=OFF' '-DNCNN_XOP=OFF' '-DNCNN_INT8=OFF' '-DNCNN_BF16=OFF' '-DGLSLANG_TESTS=OFF' '-DENABLE_GLSLANG_BINARIES=OFF' '-DENABLE_OPT=OFF' '-DCMAKE_POLICY_VERSION_MINIMUM=3.5'
if ($LASTEXITCODE -ne 0) { throw 'CMake configuration failed.' }
& $Cmake --build $Build -j 16
if ($LASTEXITCODE -ne 0) { throw 'C++ build failed.' }
Copy-Item -LiteralPath (Join-Path $Build 'rife-poc.exe') -Destination $Destination
Get-Item (Join-Path $Destination 'rife-poc.exe') | Select-Object Name,Length
