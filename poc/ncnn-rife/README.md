# Independent RIFE/ncnn/Vulkan compatibility PoC

결과와 한계는 [REPORT.ko.md](REPORT.ko.md), 측정 원본은 [results.json](results.json)에 있습니다.
이 폴더는 GUI 재작성이 아니라 RIFE 4.25/4.26 호환성 검증입니다.
Video2X 6.4.0의 입력/출력, 패딩, warp와 정밀도 요구사항을 분석 참고했고,
프로그램 코드는 복사하거나 직접 이식하지 않았습니다. 외부 참고 소스/모델은 이 폴더에 없습니다.
모델은 프로젝트의 기존 MIT 네트워크와 공식 MIT 가중치에서 직접 변환했습니다.

## 개발 환경과 빌드

Windows x64, Vulkan 드라이버가 필요합니다. 검증 PC는 RTX 4080 SUPER입니다.
개발/변환에는 기존 Python 환경과 PyTorch가 필요하지만 **엔진 실행과 패키지에는 필요하지 않습니다**.
아래 명령은 저장소 루트에서 실행합니다. 다운로드/중간 파일은 F 드라이브에 유지합니다.

```powershell
$work = 'F:\Temp\FrameDoubler\ncnn-poc'
$env:PIP_CACHE_DIR = 'F:\Temp\FrameDoubler\pip-cache'
$env:TEMP = "$work\tmp"
$env:TMP = $env:TEMP
New-Item -ItemType Directory -Force $env:TEMP | Out-Null
.venv\Scripts\python -m pip install pnnx==20261020 cmake==4.4.4 ninja==1.13.2
pwsh -NoProfile -File poc\ncnn-rife\bootstrap.ps1 -WorkRoot $work
pwsh -NoProfile -File poc\ncnn-rife\build.ps1 -WorkRoot $work
foreach ($model in 'v4.25','v4.26') {
    .venv\Scripts\python poc\ncnn-rife\export_model.py --model $model --scale 1 --output "$work\models\$model-vulkan"
    .venv\Scripts\python poc\ncnn-rife\export_model.py --model $model --scale 0.5 --output "$work\models\$model-vulkan-s05"
}
.venv\Scripts\python poc\ncnn-rife\package.py --work $work
.venv\Scripts\python poc\ncnn-rife\smoke.py --package "$work\package\FrameDoubler-ncnn-PoC"
```

`bootstrap.ps1`은 ncnn 20261020, 해당 glslang 커밋, LLVM-MinGW 20261104
다운로드의 SHA-256을 검사합니다. 소스는 저장소 밖에 둡니다. MSVC/Vulkan SDK 없이
`NCNN_SIMPLEVK`와 정적 ncnn/glslang으로 빌드합니다. Vulkan은 GPU 드라이버의 로더를 사용합니다.
도구/변환기/학습 가중치/소스/중간 `.pt` 파일은 패키지에 복사하지 않습니다.
`package.py`는 기존 출력 폴더를 덮어쓰지 않습니다. 이전 패키지를 보존하고 새 검증 폴더를 사용합니다.
버전별 scale 1/0.5의 가중치가 같은 것을 SHA로 확인한 뒤 가중치 한 벌과 param 두 개를 담습니다.

## 재현 측정

`compare.py`와 `video_compare.py`는 개발 전용 비교 도구입니다. 기존 실제 샘플
`samples/Fire Emblem Fortunes Weave 30fps.mp4`가 필요합니다.
GPU 측정은 다른 GPU 작업과 겹치지 않게 순서대로 실행합니다.

```powershell
foreach ($model in 'v4.25','v4.26') {
    foreach ($size in '1920x1080','2560x1440','3840x2160') {
        .venv\Scripts\python poc\ncnn-rife\compare.py --model $model --size $size --repeats 12
    }
    foreach ($pair in 120,360) {
        .venv\Scripts\python poc\ncnn-rife\compare.py --model $model --size 3840x2160 --pair-index $pair --repeats 4
    }
    foreach ($size in '1920x1080','3840x2160') {
        .venv\Scripts\python poc\ncnn-rife\video_compare.py --model $model --size $size
    }
}
```

비교기는 같은 RGB8 두 프레임을 사용하고 t=0.25/0.5/0.75를 실행합니다.
속도 표는 예열 후 12회 중앙값입니다. C++ 요청은 파이프·두 입력 준비·Vulkan 실행·결과 반환을
포함하며, PyTorch도 두 입력 준비+추론+CPU 반환을 포함하는 수치를 별도로 잽니다.
추론 구간은 GPU 전용 타임스탬프가 아니라 ncnn extract 벽시계(업로드/실행/다운로드)입니다.
첫 요청 시간은 시작/셰이더 준비의 영향을 받으며 따로 기록합니다.
`--fp32`는 FP16 저장을 끈 대조 실험입니다. 기본은 FP16 저장+FP32 연산입니다.

영상 시험은 원본의 짝수 프레임을 239프레임/30fps로 저장한 뒤 478프레임/60fps를 만듭니다.
네이티브 경로는 OS 파이프로 기존 FFmpeg → C++ → 기존 FFmpeg를 연결하며,
Python이 보간하거나 프레임을 중계하지 않습니다. FFmpeg 설정 최적화는 하지 않습니다.
오디오는 이 엔진 시험에서 제외합니다. PyTorch 영상은 기존 pipeline을 그대로 사용합니다.

## 실행 계약

```text
rife-poc MODEL_DIR WIDTH HEIGHT fp16|fp32 gpu|cpu [SCALE] [sequence]
```

- 실제 검증/지원 경로는 `gpu`입니다. 사용자 정의 warp의 CPU 실행은 실패하도록 만들어
  의도치 않은 CPU 대체 실행을 숨기지 않습니다. 모든 비구조 레이어의 Vulkan 지원도 확인합니다.
- 입력/출력은 색상 변환 없는 packed RGB24입니다. 추론 시 planar RGB/255, replicate padding,
  dense timestep을 사용합니다. 1080p/1440p scale=1, 4K scale=0.5, 정렬은 64/scale입니다.
- 기본 요청 모드: `P` 한 바이트, little-endian float32 timestep, RGB A, RGB B를 stdin에 씁니다.
  stdout은 little-endian float64 시간 세 개(준비/extract/후처리), RGB 중간 프레임입니다.
  `Q` 또는 EOF로 종료합니다. 프레임 크기는 WIDTH×HEIGHT×3바이트입니다.
- `sequence`: stdin은 연속된 30fps RGB 프레임입니다. 각 A/B에서 A와 t=0.5 결과를 출력하고
  마지막 프레임은 두 번 출력합니다. 출력은 타임스탬프 헤더 없는 60fps RGB 스트림입니다.
- 버전/scale 조합에 맞는 모델을 지정해야 합니다. 패키지의 모델 폴더는 scale=0.5일 때
  `rife-s05.ncnn.param`을 선택하며 같은 `rife.ncnn.bin`을 공유합니다.
- HDR/10비트/VFR/오디오/취소/GUI 연동과 장시간 사용은 이 PoC의 완료 범위가 아닙니다.
  안전한 배포 API·성능 목표 달성·최종 앱 배포 검증도 후속 작업입니다.

모든 자체 코드는 저장소의 MIT 라이선스를 따릅니다. 패키지에 ncnn/glslang/LLVM·MinGW runtime/
Practical-RIFE 고지를 함께 넣습니다. 외부 구성 요소의 실제 고지는 패키지 `licenses/`를 확인합니다.
