param([Parameter(Mandatory)][string]$Output,[Parameter(Mandatory)][string]$CompilerRoot)
$ErrorActionPreference='Stop'
$Output=[IO.Path]::GetFullPath($Output)
New-Item -ItemType Directory -Force -Path $Output | Out-Null
$Compiler=Join-Path $CompilerRoot 'bin\clang++.exe'
$ResourceCompiler=Join-Path $CompilerRoot 'bin\llvm-windres.exe'
$ProjectRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$Version=([xml](Get-Content -LiteralPath (Join-Path $ProjectRoot 'app\Directory.Build.props') -Raw)).Project.PropertyGroup.Version
if($Version -notmatch '^(\d+)\.(\d+)\.(\d+)$'){throw "Invalid application version: $Version"}
$Numbers="$($Matches[1]),$($Matches[2]),$($Matches[3]),0"
$Icon=(Join-Path $ProjectRoot 'assets\icons\app-icon.ico').Replace('\','/')
# Same execution level as the .NET apphost used up to 1.0.1 (no installer-detection elevation).
$Manifest=Join-Path $Output 'launcher.manifest'
@'
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0">
  <trustInfo xmlns="urn:schemas-microsoft-com:asm.v2">
    <security>
      <requestedPrivileges xmlns="urn:schemas-microsoft-com:asm.v3">
        <requestedExecutionLevel level="asInvoker" uiAccess="false"/>
      </requestedPrivileges>
    </security>
  </trustInfo>
  <compatibility xmlns="urn:schemas-microsoft-com:compatibility.v1">
    <application>
      <supportedOS Id="{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}"/>
    </application>
  </compatibility>
</assembly>
'@ | Set-Content -LiteralPath $Manifest -Encoding utf8
foreach($Part in @('gui','cli')){
    $Name=if($Part -eq 'gui'){'FrameDoubler'}else{'FrameDoubler.Cli'}
    $Resource=Join-Path $Output "launcher-$Part.rc"
    # Version strings match the earlier apphost so Task Manager and file properties show the same names.
    @"
1 ICON "$Icon"
1 24 "$($Manifest.Replace('\','/'))"
1 VERSIONINFO
FILEVERSION $Numbers
PRODUCTVERSION $Numbers
FILEOS 0x40004
FILETYPE 0x1
BEGIN
  BLOCK "StringFileInfo"
  BEGIN
    BLOCK "040904b0"
    BEGIN
      VALUE "CompanyName", "FrameDoubler"
      VALUE "FileDescription", "$Name"
      VALUE "FileVersion", "$Version.0"
      VALUE "InternalName", "$Name.exe"
      VALUE "OriginalFilename", "$Name.exe"
      VALUE "ProductName", "$Name"
      VALUE "ProductVersion", "$Version"
    END
  END
  BLOCK "VarFileInfo"
  BEGIN
    VALUE "Translation", 0x409, 1200
  END
END
"@ | Set-Content -LiteralPath $Resource -Encoding utf8
    $Compiled=Join-Path $Output "launcher-$Part.res"
    & $ResourceCompiler -i $Resource -o $Compiled -O coff
    if($LASTEXITCODE -ne 0){throw "Launcher resource compilation failed: $Part"}
    $Arguments=@('-std=c++17','-O2','-static','-municode',(Join-Path $PSScriptRoot 'main.cpp'),$Compiled,'-lshell32')
    if($Part -eq 'gui'){$Arguments+=@('-DFD_GUI','-mwindows')}
    $Arguments+=@('-o',(Join-Path $Output "$Name.exe"))
    & $Compiler @Arguments
    if($LASTEXITCODE -ne 0){throw "Native .NET launcher compilation failed: $Part"}
}
