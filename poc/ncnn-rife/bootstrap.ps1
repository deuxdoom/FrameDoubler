param([string]$WorkRoot = (Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'tools\dev\ncnn'))
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $WorkRoot | Out-Null

function Get-PinnedArchive($Name, $Url, $Hash) {
    $Path = Join-Path $WorkRoot $Name
    if (-not (Test-Path -LiteralPath $Path)) { Invoke-WebRequest -Uri $Url -OutFile $Path }
    if ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ne $Hash) {
        throw "Checksum mismatch: $Path"
    }
    return $Path
}
$Compiler = Get-PinnedArchive 'llvm-mingw.zip' 'https://github.com/mstorsjo/llvm-mingw/releases/download/20261104/llvm-mingw-20261104-ucrt-x86_64.zip' '552191B5754C49A5A20723276A6FC56541EA1922C4C50B7B669022333FC1B40F'
$Ncnn = Get-PinnedArchive 'ncnn-source.zip' 'https://github.com/Tencent/ncnn/archive/refs/tags/20261020.zip' 'CA7643D59D50B8FF455EDFE4B08C64DA0355028EF57D87AC8FA5264D1D18E4AB'
$Glslang = Get-PinnedArchive 'glslang.zip' 'https://github.com/KhronosGroup/glslang/archive/780b6a209132dc74e99458f62ebe5fa7e57c41cf.zip' 'D2433127A3CD6D91EF1CD2B4D186A110672011A40B33B8527156482D54CFAD79'
$CompilerRoot = Join-Path $WorkRoot 'compiler'
$Native = Join-Path $WorkRoot 'native'
if (-not (Test-Path (Join-Path $CompilerRoot 'llvm-mingw-20261104-ucrt-x86_64\bin\clang++.exe'))) {
    Expand-Archive -LiteralPath $Compiler -DestinationPath $CompilerRoot
}
$Source = Join-Path $Native 'ncnn-20261020'
if (-not (Test-Path (Join-Path $Source 'CMakeLists.txt'))) {
    Expand-Archive -LiteralPath $Ncnn -DestinationPath $Native
}
if (-not (Test-Path (Join-Path $Source 'glslang\CMakeLists.txt'))) {
    $Stage = Join-Path $WorkRoot 'glslang-stage'
    Expand-Archive -LiteralPath $Glslang -DestinationPath $Stage -Force
    $GlslangSource = Join-Path $Stage 'glslang-780b6a209132dc74e99458f62ebe5fa7e57c41cf'
    Copy-Item -Path (Join-Path $GlslangSource '*') -Destination (Join-Path $Source 'glslang') -Recurse -Force
}
Write-Output 'Pinned development sources and compiler ready. Run build.ps1 next.'
