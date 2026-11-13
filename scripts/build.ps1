param(
    [string]$OutputRoot = 'F:\Temp\FrameDoubler\package',
    [string]$DistPath
)
$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
$Python = Join-Path $Root '.venv\Scripts\python.exe'
if (-not (Test-Path -LiteralPath $Python)) { throw 'Run scripts\setup.ps1 first.' }
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
if ($OutputRoot.StartsWith([IO.Path]::GetFullPath($Root) + '\', [StringComparison]::OrdinalIgnoreCase) -or $OutputRoot -eq $Root) {
    throw 'Build outputs must be outside the repository.'
}
$BuildRoot = Join-Path $OutputRoot (Get-Date -Format 'yyyyMMdd-HHmmss-fff')
if ($DistPath) {
    if (-not [IO.Path]::IsPathRooted($DistPath)) { $DistPath = Join-Path $Root $DistPath }
    $DistPath = [IO.Path]::GetFullPath($DistPath)
    $RepositoryDist = Join-Path $Root 'dist'
    $InsideRepository = $DistPath -eq $Root -or $DistPath.StartsWith($Root + '\', [StringComparison]::OrdinalIgnoreCase)
    $InsideDist = $DistPath -eq $RepositoryDist -or $DistPath.StartsWith($RepositoryDist + '\', [StringComparison]::OrdinalIgnoreCase)
    if ($InsideRepository -and -not $InsideDist) { throw 'Repository outputs must be under dist.' }
} else {
    $DistPath = Join-Path $BuildRoot 'dist'
}
$Bundle = Join-Path $DistPath 'FrameDoubler'
if (Test-Path -LiteralPath $Bundle) { throw "Bundle already exists: $Bundle. Choose an empty destination." }
New-Item -ItemType Directory -Path $BuildRoot -Force | Out-Null
$env:PIP_CACHE_DIR = Join-Path $OutputRoot 'pip-cache'
$env:TEMP = Join-Path $OutputRoot 'temp'
$env:TMP = $env:TEMP
$env:PYINSTALLER_CONFIG_DIR = Join-Path $OutputRoot 'pyinstaller-cache'
New-Item -ItemType Directory -Path $env:TEMP -Force | Out-Null
& $Python -m pip install -r (Join-Path $PSScriptRoot 'requirements-build.txt')
if ($LASTEXITCODE -ne 0) { throw 'Could not install build tools.' }
& $Python -m PyInstaller --distpath $DistPath --workpath (Join-Path $BuildRoot 'work') (Join-Path $PSScriptRoot 'FrameDoubler.spec')
if ($LASTEXITCODE -ne 0) { throw 'PyInstaller failed.' }
& $Python (Join-Path $PSScriptRoot 'stage_package.py') --bundle $Bundle --legal-cache (Join-Path $OutputRoot 'license-cache')
if ($LASTEXITCODE -ne 0) { throw 'Package staging failed.' }
Set-Content -LiteralPath (Join-Path $OutputRoot 'latest-build.txt') -Value $Bundle -Encoding utf8
Write-Output "Bundle: $Bundle"
Write-Output 'This is a local candidate; clean Windows 11 validation is still required.'
