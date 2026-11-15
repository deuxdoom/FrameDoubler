param(
    [string]$DistPath = 'dist\FrameDoubler',
    [string]$WorkRoot = $(if($env:FRAMEDOUBLER_TEMP_ROOT){Join-Path $env:FRAMEDOUBLER_TEMP_ROOT 'package'}else{Join-Path (Split-Path -Parent $PSScriptRoot) 'build\package'}),
    [string]$PocWorkRoot = (Join-Path (Split-Path -Parent $PSScriptRoot) 'tools\dev\ncnn'),
    [switch]$SkipAcceleration,
    # NVIDIA VFX is an optional in-app download; bundle it only for a local test build.
    [switch]$IncludeNvidiaVfx
)
$ErrorActionPreference='Stop'
$ProjectRoot=Split-Path -Parent $PSScriptRoot
$Destination=if([IO.Path]::IsPathRooted($DistPath)){[IO.Path]::GetFullPath($DistPath)}else{[IO.Path]::GetFullPath((Join-Path $ProjectRoot $DistPath))}
if(Test-Path -LiteralPath $Destination){throw "Destination already exists; preserve it and select a new path: $Destination"}
$Stamp=Get-Date -Format 'yyyyMMdd-HHmmss'
$Build=Join-Path $WorkRoot ($Stamp+'-'+[Guid]::NewGuid().ToString('N').Substring(0,6))
New-Item -ItemType Directory -Force -Path $Build,(Join-Path $WorkRoot 'tmp') | Out-Null
$env:TEMP=Join-Path $WorkRoot 'tmp';$env:TMP=$env:TEMP
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1';$env:DOTNET_GENERATE_ASPNET_CERTIFICATE='false'
$env:DOTNET_CLI_HOME=Join-Path $WorkRoot 'dotnet-home';$env:DOTNET_NOLOGO='1'
$env:FRAMEDOUBLER_BUILD_ROOT=Join-Path $WorkRoot 'dotnet'
$BuildTools=Join-Path (Split-Path -Parent $PocWorkRoot) 'build-env'
$Python=Join-Path $BuildTools 'Scripts\python.exe'
if(-not(Test-Path -LiteralPath $Python)){throw 'Run pwsh scripts/setup-dev.ps1 before building'}
Push-Location (Join-Path $ProjectRoot 'app')
try {
    & dotnet publish FrameDoubler.App\FrameDoubler.App.csproj -c Release -r win-x64 --self-contained true -o (Join-Path $Build 'gui') '-p:DebugType=None' '-p:DebugSymbols=false' '-p:SatelliteResourceLanguages=ko%3Ben'
    if($LASTEXITCODE -ne 0){throw 'GUI publish failed'}
    & dotnet publish FrameDoubler.Cli\FrameDoubler.Cli.csproj -c Release -r win-x64 --self-contained true -o (Join-Path $Build 'cli') '-p:DebugType=None' '-p:DebugSymbols=false' '-p:SatelliteResourceLanguages=ko%3Ben'
    if($LASTEXITCODE -ne 0){throw 'CLI publish failed'}
} finally {Pop-Location}
& (Join-Path $ProjectRoot 'poc\ncnn-rife\build.ps1') -WorkRoot $PocWorkRoot
if($LASTEXITCODE -ne 0){throw 'Native engine build failed'}
if(-not $SkipAcceleration){
    & (Join-Path $ProjectRoot 'poc\ort-rife\build.ps1')
    if($LASTEXITCODE -ne 0){throw 'Optional acceleration worker build failed'}
}
# Live capture engine (poc/live): fdlive.dll is loaded by the GUI's capture preview.
& (Join-Path $ProjectRoot 'poc\live\build.ps1') -Output (Join-Path $Build 'live')
if($LASTEXITCODE -ne 0){throw 'Live capture engine build failed'}
$Assets=Join-Path $Build 'engine-assets'
& $Python (Join-Path $ProjectRoot 'poc\ncnn-rife\package.py') --work $PocWorkRoot --output $Assets
if($LASTEXITCODE -ne 0){throw 'Model and engine staging failed'}
New-Item -ItemType Directory -Force -Path $Destination,(Join-Path $Destination 'dll'),(Join-Path $Destination 'engine'),(Join-Path $Destination 'ffmpeg') | Out-Null
foreach($Part in 'gui','cli'){
    Get-ChildItem -LiteralPath (Join-Path $Build $Part) | ForEach-Object {
        if($_.Name -in 'FrameDoubler.exe','FrameDoubler.Cli.exe'){return}
        $Target=Join-Path (Join-Path $Destination 'dll') $_.Name
        if((Test-Path -LiteralPath $Target) -and -not $_.PSIsContainer){
            if((Get-FileHash -LiteralPath $_.FullName).Hash -ne (Get-FileHash -LiteralPath $Target).Hash){throw "Conflicting publish file: $($_.Name)"}
        } else {Copy-Item -LiteralPath $_.FullName -Destination $Target -Recurse -Force}
    }
}
& (Join-Path $ProjectRoot 'poc\dotnet-launcher\build.ps1') -Output (Join-Path $Build 'launchers') -CompilerRoot (Join-Path $PocWorkRoot 'compiler\llvm-mingw-20261104-ucrt-x86_64')
if($LASTEXITCODE -ne 0){throw 'Native .NET launcher build failed'}
Copy-Item -LiteralPath (Join-Path $Build 'launchers\FrameDoubler.exe'),(Join-Path $Build 'launchers\FrameDoubler.Cli.exe') -Destination $Destination
Copy-Item -LiteralPath (Join-Path $Assets 'rife-poc.exe') -Destination (Join-Path $Destination 'engine\rife-poc.exe')
Copy-Item -LiteralPath (Join-Path $Build 'live\fdlive.dll') -Destination (Join-Path $Destination 'engine\fdlive.dll')
# The same engine as a command-line helper: the GUI asks it for the driver's DLSS-G limit in a
# separate process, so a fault inside the driver cannot close the program.
Copy-Item -LiteralPath (Join-Path $Build 'live\fd-live.exe') -Destination (Join-Path $Destination 'engine\fd-live.exe')

Copy-Item -LiteralPath (Join-Path $Assets 'models'),(Join-Path $Assets 'licenses') -Destination $Destination -Recurse
# DLSS Frame Generation feature library from NVIDIA's public DLSS repository (the NGX core
# itself is part of the NVIDIA driver); tools/dev/dlss/download-manifest.json holds its hash.
$DlssManifest=Get-Content -LiteralPath (Join-Path $ProjectRoot 'tools\dev\dlss\download-manifest.json') -Raw | ConvertFrom-Json
$DlssSource=Join-Path $ProjectRoot ('tools\dev\dlss\'+$DlssManifest.runtime.file)
if((Get-FileHash -LiteralPath $DlssSource).Hash.ToLowerInvariant() -ne $DlssManifest.runtime.sha256){throw 'DLSS frame generation library hash mismatch'}
New-Item -ItemType Directory -Force -Path (Join-Path $Destination 'engine\dlss'),(Join-Path $Destination 'licenses\nvidia-dlss') | Out-Null
Copy-Item -LiteralPath $DlssSource -Destination (Join-Path $Destination 'engine\dlss\nvngx_dlssg.dll')
Copy-Item -LiteralPath (Join-Path $ProjectRoot 'tools\dev\dlss\include\LICENSE.txt') -Destination (Join-Path $Destination 'licenses\nvidia-dlss\LICENSE.txt')
# NVIDIA VFX frame generation: only the five DLLs VideoFrameGeneration needs, with NVIDIA's
# license files (tools/dev/nvvfx/download-manifest.json holds their sizes and hashes).
if($IncludeNvidiaVfx){
    $VfxRoot=Join-Path $ProjectRoot 'tools\dev\nvvfx\runtime-0.2.0.0'
    $VfxManifest=Get-Content -LiteralPath (Join-Path $ProjectRoot 'tools\dev\nvvfx\download-manifest.json') -Raw | ConvertFrom-Json
    New-Item -ItemType Directory -Force -Path (Join-Path $Destination 'engine\nvvfx'),(Join-Path $Destination 'licenses\nvidia-vfx') | Out-Null
    foreach($File in $VfxManifest.frame_generation_files){
        $Source=Join-Path $VfxRoot ('nvvfx\libs\'+$File.file)
        if((Get-FileHash -LiteralPath $Source).Hash.ToLowerInvariant() -ne $File.sha256){throw "NVIDIA VFX file hash mismatch: $($File.file)"}
        Copy-Item -LiteralPath $Source -Destination (Join-Path $Destination ('engine\nvvfx\'+$File.file))
    }
    foreach($File in $VfxManifest.license_files){
        Copy-Item -LiteralPath (Join-Path $VfxRoot ('nvidia_vfx-0.2.0.0.dist-info\licenses\packaging\'+$File.file)) -Destination (Join-Path $Destination ('licenses\nvidia-vfx\'+$File.file))
    }
}
if(-not $SkipAcceleration){
Copy-Item -LiteralPath (Join-Path $ProjectRoot 'tools\dev\onnx\bin\rife-ort.exe') -Destination (Join-Path $Destination 'engine\rife-ort.exe')
$OnnxModels=Join-Path $ProjectRoot 'tools\dev\onnx\package-models'
if(-not(Test-Path -LiteralPath (Join-Path $OnnxModels 'v4.25-s1.onnx'))){throw 'Prepare shared FP32 ONNX model assets with poc/ort-rife/package_models.py'}
Copy-Item -LiteralPath $OnnxModels -Destination (Join-Path $Destination 'models\onnx') -Recurse
Copy-Item -LiteralPath (Join-Path $ProjectRoot 'poc\ort-rife\ONNXRuntime-LICENSE.txt') -Destination (Join-Path $Destination 'licenses\ONNXRuntime-MIT.txt')
}
Copy-Item -LiteralPath (Join-Path $ProjectRoot 'tools\ffmpeg\bin\ffmpeg.exe'),(Join-Path $ProjectRoot 'tools\ffmpeg\bin\ffprobe.exe') -Destination (Join-Path $Destination 'ffmpeg')
Copy-Item -LiteralPath (Join-Path $ProjectRoot 'tools\ffmpeg\LICENSE.txt') -Destination (Join-Path $Destination 'licenses\FFmpeg-LGPL.txt')
Copy-Item -LiteralPath (Join-Path $ProjectRoot 'app\NATIVE-NOTICES.md') -Destination (Join-Path $Destination 'THIRD_PARTY_NOTICES.md')
Copy-Item -LiteralPath (Join-Path $ProjectRoot 'app\README.md') -Destination (Join-Path $Destination 'README.md')
Copy-Item -LiteralPath (Join-Path $ProjectRoot 'app\HARDWARE-TESTS.md') -Destination (Join-Path $Destination 'HARDWARE-TESTS.md')
Copy-Item -LiteralPath (Join-Path $ProjectRoot 'app\FrameDoubler.App\Assets\Fonts\OFL.txt') -Destination (Join-Path $Destination 'licenses\PretendardJP-OFL.txt')
& (Join-Path $Destination 'ffmpeg\ffmpeg.exe') -version | Set-Content -LiteralPath (Join-Path $Destination 'ffmpeg-version.txt') -Encoding utf8
& (Join-Path $Destination 'ffmpeg\ffmpeg.exe') -buildconf 2>&1 | Set-Content -LiteralPath (Join-Path $Destination 'ffmpeg-buildconf.txt') -Encoding utf8
$NoticeRoot=Join-Path $Destination 'licenses\dotnet'
New-Item -ItemType Directory -Force $NoticeRoot | Out-Null
$Cache=[IO.Path]::GetFullPath((Join-Path $env:FRAMEDOUBLER_BUILD_ROOT '..\nuget-cache'))
foreach($Package in 'microsoft.netcore.app.runtime.win-x64','microsoft.windowsdesktop.app.runtime.win-x64'){
    $PackageRoot=Join-Path $Cache $Package
    if(Test-Path -LiteralPath $PackageRoot){
        Get-ChildItem -LiteralPath $PackageRoot -Directory | ForEach-Object {
            $Version=$_.Name
            Get-ChildItem -LiteralPath $_.FullName -File | Where-Object {$_.Name -match 'LICENSE|THIRD-PARTY|THIRDPARTY'} | ForEach-Object {
                Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $NoticeRoot ($Package+'-'+$Version+'-'+$_.Name))
            }
        }
    }
}
$Extra=@{
    'OpenH264-BSD.txt'='https://raw.githubusercontent.com/cisco/openh264/master/LICENSE';
    'Kvazaar-BSD.txt'='https://raw.githubusercontent.com/ultravideo/kvazaar/master/LICENSE';
    'SVT-AV1.txt'='https://raw.githubusercontent.com/AOMediaCodec/SVT-AV1/main/LICENSE.md'
}
foreach($Name in $Extra.Keys){Invoke-WebRequest -Uri $Extra[$Name] -OutFile (Join-Path $Destination ('licenses\'+$Name))}
$Inventory=@(Get-ChildItem -LiteralPath $Destination -File -Recurse | Sort-Object FullName | ForEach-Object {
    [ordered]@{path=[IO.Path]::GetRelativePath($Destination,$_.FullName).Replace('\','/');bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant()}
})
# The NVIDIA VFX runtime in engine/nvvfx needs its own CUDA runtime DLL; nothing else may carry one.
if(@($Inventory.path | Where-Object {$_ -notlike 'engine/nvvfx/*'}) -match '(?i)(torch|python|cudnn|cublas|cudart|\.pkl$|\.pt$)'){throw 'Forbidden legacy runtime/model file in native package'}
$Total=($Inventory | Measure-Object -Property bytes -Sum).Sum
$SourceFiles=@(& git -C $ProjectRoot ls-files --cached --others --exclude-standard -- app poc/ncnn-rife poc/ort-rife poc/dotnet-launcher poc/live |
    Where-Object {[IO.Path]::GetExtension($_) -in '.cs','.xaml','.csproj','.cpp','.h','.ps1','.py','.json','.props','.rc','.manifest'} | Sort-Object | ForEach-Object {
        [ordered]@{path=$_;sha256=(Get-FileHash -LiteralPath (Join-Path $ProjectRoot $_)).Hash.ToLowerInvariant()}
    })
if($SourceFiles.Count -eq 0){throw 'No tracked source files found'}
$Revision=(& git -C $ProjectRoot rev-parse HEAD).Trim()
$SourceDirty=[bool](& git -C $ProjectRoot status --porcelain -- app poc/ncnn-rife poc/ort-rife poc/dotnet-launcher poc/live scripts/build-native.ps1)
$AppVersion=([xml](Get-Content -LiteralPath (Join-Path $ProjectRoot 'app\Directory.Build.props') -Raw)).Project.PropertyGroup.Version
if($AppVersion -notmatch '^\d+\.\d+\.\d+$'){throw "Invalid application version: $AppVersion"}
$Manifest=[ordered]@{format='framedoubler-native-v1';version=$AppVersion;runtime='win-x64 self-contained .NET 10';acceleration_engine=(-not $SkipAcceleration);live_engine=$true;nvidia_vfx=[bool]$IncludeNvidiaVfx;nvidia_dlss_fg=$true;source_commit=$Revision;source_dirty=$SourceDirty;source_files=$SourceFiles;created_utc=[DateTime]::UtcNow.ToString('o');bytes_without_manifest=$Total;files=$Inventory}
$Manifest|ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $Destination 'manifest.json') -Encoding utf8
$Zip=Join-Path (Split-Path -Parent $Destination) ('FrameDoubler_v'+$AppVersion.Replace('.','')+'.zip')
if(Test-Path -LiteralPath $Zip){throw "Archive already exists: $Zip"}
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($Destination,$Zip,[IO.Compression.CompressionLevel]::Optimal,$true)
(Get-FileHash -LiteralPath $Zip).Hash.ToLowerInvariant()+'  '+[IO.Path]::GetFileName($Zip) | Set-Content -LiteralPath ($Zip+'.sha256') -Encoding ascii
$Actual=(Get-ChildItem -LiteralPath $Destination -File -Recurse | Measure-Object -Property Length -Sum).Sum
Write-Output "FrameDoubler package: $Destination"
Write-Output "Installed bytes: $Actual; ZIP bytes: $((Get-Item -LiteralPath $Zip).Length)"
