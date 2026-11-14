# 독립 RIFE/ncnn/Vulkan 엔진과 개발 도구

Video2X 6.4.0을 읽기 전용 기술 참고로 분석했으며 코드·모델 자산을 복사/이식하지 않았습니다.
공식 MIT RIFE 4.25/4.26 가중치를 이 프로젝트의 MIT 네트워크에서 독립 변환합니다.
제품 GUI/CLI와 회귀 시험은 `app/`에 있습니다.

## 기본 빌드

```powershell
pwsh scripts/setup-dev.ps1
pwsh poc/ncnn-rife/bootstrap.ps1
pwsh poc/ncnn-rife/build.ps1
pwsh scripts/build.ps1
```

CMake/Ninja는 프로젝트의 `tools/dev/build-env`에 둡니다. 기본 빌드에 Torch/Qt가 필요하지 않습니다.
`bootstrap.ps1`은 SHA-256으로 고정한 ncnn 20261020·glslang·LLVM-MinGW를 `tools/dev/ncnn`에 준비합니다.
Vulkan SDK 없이 `NCNN_SIMPLEVK`와 정적 ncnn/glslang을 빌드합니다. CPU 대체도 지원합니다.
FFmpeg(`tools/ffmpeg`)와 변환한 모델(`tools/dev/ncnn/models`)은 로컬 빌드 입력입니다. `package.py`는 엔진·변환 모델·고지를 준비하며
최종 `scripts/build.ps1`은 .NET 런타임을 포함한 `dist/FrameDoubler`와 ZIP을 만듭니다.
개발 의존 파일과 도구는 프로젝트 안에 보관하며, 삭제 가능한 빌드 산출물·NuGet 캐시는 `build/`에 둡니다.
외부 Temp 폴더는 빌드 입력이 아닙니다. `tools/`·`build/`는 Git과 제품 배포에 포함하지 않습니다.

## 선택적 최초 모델 변환

`baseline/rife.py`·`baseline/weights.py`는 `export_model.py`의 모델 변환에만 쓰는 최소 MIT 네트워크 정의입니다.
Python/Torch/pnnx·학습 가중치는 배포하지 않습니다. 내려받은 모델의 Python 코드는 실행하지 않으며
공식 가중치는 SHA-256 확인 후 `torch.load(weights_only=True)`로만 읽습니다.

변환 환경에는 `requirements-comparison.txt`와 장치에 맞는 공식 Torch 빌드를 설치합니다.

```powershell
$py='./tools/dev/comparison-env/Scripts/python.exe'
$work='./tools/dev/ncnn'
foreach($model in 'v4.25','v4.26') {
    & $py poc/ncnn-rife/export_model.py --model $model --scale 1 --output "$work/models/$model-vulkan"
    & $py poc/ncnn-rife/export_model.py --model $model --scale 0.5 --output "$work/models/$model-vulkan-s05"
}
```

선택적 비교 환경은 `python -m venv tools/dev/comparison-env`로 만들고 위 requirements와
공식 Torch 빌드를 설치합니다. 학습 가중치 기본 경로는 `tools/dev/ncnn/weights`이며
`FRAMEDOUBLER_MODELS`로 재지정할 수 있습니다. 기존 Temp의 가상 환경은 필요하지 않습니다.

제품 변환·오디오·취소 검증은 `app/FrameDoubler.Tests`의 C# 테스트를 사용합니다.

## 선택적 ONNX 모델 변환

`requirements-onnx.txt`는 개발용 ONNX 변환 의존성입니다. 제품 빌드·실행에는 필요하지 않습니다.
공식 가중치가 `tools/dev/ncnn/weights`에 있어야 하며 SHA-256이 다르거나 없으면 변환을 중단합니다.
변환기가 가중치를 자동으로 다운로드하지 않습니다.

```powershell
$py='./tools/dev/comparison-env/Scripts/python.exe'
& $py poc/ncnn-rife/export_onnx.py --model v4.25 --scale 0.5 --output tools/dev/onnx/models/v4.25-s0.5.onnx
& $py poc/ncnn-rife/convert_onnx_rgb8.py tools/dev/onnx/models/v4.25-s0.5.onnx tools/dev/onnx/models/v4.25-s0.5-rgb8.onnx
```

두 모델과 scale 1/0.5를 지원합니다. 기본 ONNX는 FP32 NCHW 영상 두 장과 스칼라 시간값을 받아
표준 ONNX 연산(opset 18)으로 보간합니다. RGB8 변환은 NHWC uint8 입출력의 형식 변환·정규화만
그래프 안으로 옮기며 네트워크·warp·grid는 FP32입니다. 호출자가 원본 크기를 유지한 채 기존과 같은
64/scale replicate padding을 적용하고, 결과의 패딩만 제거합니다. 보간 전 해상도 축소는 하지 않습니다.
현재 앱의 실행 엔진은 계속 ncnn이며 ONNX 파일·ORT 런타임은 제품에 포함하지 않습니다.

## 엔진 계약

```text
rife-poc --hardware
rife-poc MODEL_DIR WIDTH HEIGHT fp16|fp32 auto|gpu:INDEX|cpu [SCALE] [rgb16] [sequence]
```

- RGB 0~1, 64/scale replicate padding, dense timestep, FP16 저장/FP32 연산. 4K scale=0.5.
- 100만 픽셀 이상에서는 RGB 입출력 변환의 행을 최대 4개 CPU 스레드로 나눕니다(논리 프로세서 수의 1/4 상한). 픽셀별 계산·패딩·출력 순서는 유지합니다. 더 작은 프레임은 한 스레드입니다.
- `rgb16`은 RGB48 little-endian이며 FP32 저장/연산. FP16 저장은 실제 장치 기능 확인 후 선택합니다.
- `P` + little-endian float32 timestep + RGB A/B 요청. 응답은 float64 시간 세 개 + RGB 중간 프레임.
  `Q`/EOF로 종료합니다. `sequence`는 연속 RGB30→RGB60 스트림이며 마지막 프레임을 유지합니다.
- scale별 param 두 개가 같은 bin을 공유합니다. 일반 ncnn GridSample의 CPU 대체를 피하려고
  독립 FDWarp CPU/Vulkan 커널을 사용합니다.
- 엔진 실행에 Python/Torch/.NET은 필요하지 않습니다. 제품 앱은 포함된 .NET 런타임을 사용합니다.
- 다른 제조사의 실제 장비는 아직 검증하지 않았습니다. 시험 절차는 `app/HARDWARE-TESTS.md`에 있습니다.

모든 자체 코드는 MIT이며 패키지 `licenses/`에 외부 구성 요소 고지를 포함합니다.
