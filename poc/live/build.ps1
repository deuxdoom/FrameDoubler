param(
    [string]$Output=(Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'build\live'),
    [string]$CompilerRoot=(Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'tools\dev\ncnn\compiler\llvm-mingw-20261104-ucrt-x86_64')
)
# Builds the live capture engine twice from the same sources: fdlive.dll for the app and
# fd-live.exe for command-line checks (device list, self-test, replay, capture probe).
$ErrorActionPreference='Stop'
$Output=[IO.Path]::GetFullPath($Output)
New-Item -ItemType Directory -Force -Path $Output | Out-Null
# A workspace can select its scratch root without baking a machine path into releases.
$LiveScratch=if($env:FRAMEDOUBLER_TEMP_ROOT){Join-Path $env:FRAMEDOUBLER_TEMP_ROOT ('live-build-'+[Guid]::NewGuid().ToString('N'))}else{Join-Path $Output 'tmp'}
$env:TEMP=Join-Path $LiveScratch 'tmp';$env:TMP=$env:TEMP
New-Item -ItemType Directory -Force -Path $env:TEMP | Out-Null
$Compiler=Join-Path $CompilerRoot 'bin\clang++.exe'
$Windres=Join-Path $CompilerRoot 'bin\x86_64-w64-mingw32-windres.exe'
if(Test-Path -LiteralPath (Join-Path $PSScriptRoot '../../tools/dev/nvof/include/nvOpticalFlowD3D11.h')) {
    Write-Output 'Hardware optical flow: official SDK headers found (driver capability checked at runtime)'
} else { Write-Warning 'Official NVOF headers absent: DLSS uses internal motion estimation' }
$Sources=@('capture.cpp','capture_dshow.cpp','audio.cpp','gpu.cpp','recorder.cpp','vfx.cpp','dlss.cpp','nvof.cpp','edgeaegis.cpp','engine.cpp') | ForEach-Object {Join-Path $PSScriptRoot $_}
$Common=@('-std=c++20','-O2','-static','-municode','-Wall','-Wno-unused-function','-Wno-missing-braces','-DUNICODE','-D_UNICODE','-D_WIN32_WINNT=0x0A00')
$Libs=@('-ld3d11','-ld3d12','-ldxgi','-ladvapi32','-ld3dcompiler_47','-lmfplat','-lmfreadwrite','-lmf','-lmfuuid','-lole32','-loleaut32','-luuid','-lavrt','-lwinmm','-lwindowscodecs','-lpropsys','-lshlwapi','-lstrmiids','-luser32','-lgdi32')
$Resource=Join-Path $LiveScratch 'live-resource.o'
& $Windres "-I$PSScriptRoot" '-i' (Join-Path $PSScriptRoot 'live.rc') '-o' $Resource '-O' 'coff'
if($LASTEXITCODE -ne 0){throw 'Live engine resource compilation failed'}
& $Compiler @Common '-shared' '-DFDL_DLL' @Sources '-o' (Join-Path $Output 'fdlive.dll') "-Wl,--out-implib,$(Join-Path $Output 'libfdlive.a')" @Libs
if($LASTEXITCODE -ne 0){throw 'fdlive.dll build failed'}
& $Compiler @Common @Sources (Join-Path $PSScriptRoot 'main.cpp') $Resource '-o' (Join-Path $Output 'fd-live.exe') @Libs
if($LASTEXITCODE -ne 0){throw 'fd-live.exe build failed'}
Write-Output "Live engine: $Output"
