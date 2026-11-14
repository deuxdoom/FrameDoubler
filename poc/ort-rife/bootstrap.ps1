param([string]$WorkRoot=(Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'tools/dev/onnx'),[switch]$DownloadHeaders)
$ErrorActionPreference='Stop'
$Commit='fab30a46491a40f5e4c968b80e8a59acbbb91b53'
$Files=@{
 'onnxruntime_c_api.h'='4a8df643e1667147db6a9de16570aac452cc17fb2423984530c964363d6ff5f8'
 'onnxruntime_error_code.h'='5ce3b054e798eced8d14f5b86e98692fd33470463f96194ce0700a2d53dd8721'
 'onnxruntime_ep_c_api.h'='7ca769301114baeb8cb7e0aed901b44a0b3726079fd908daada68fb47bd33bb2'
}
$Include=Join-Path $WorkRoot 'include'
foreach($Name in $Files.Keys){
 $Target=Join-Path $Include $Name
 if(-not(Test-Path -LiteralPath $Target)){
  if(-not $DownloadHeaders){throw 'ORT headers missing. Explicitly use -DownloadHeaders to fetch the pinned MIT C API definitions (605,740 bytes).'}
  New-Item -ItemType Directory -Force -Path $Include | Out-Null
  Invoke-WebRequest -Uri "https://raw.githubusercontent.com/microsoft/onnxruntime/$Commit/include/onnxruntime/core/session/$Name" -OutFile $Target
 }
 if((Get-FileHash -LiteralPath $Target).Hash.ToLowerInvariant() -ne $Files[$Name]){throw "ORT header SHA-256 mismatch: $Name"}
}
