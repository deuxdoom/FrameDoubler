# 독립 RIFE/ncnn/Vulkan 엔진과 개발 도구

호환성 결과는 [REPORT.ko.md](REPORT.ko.md)·[results.json](results.json), 성능 검증은 [N2.md](N2.md)입니다.
Video2X 6.4.0을 읽기 전용 기술 참고로 분석했으며 코드·모델 자산을 복사/이식하지 않았습니다.
공식 MIT RIFE 4.25/4.26 가중치를 이 프로젝트의 MIT 네트워크에서 독립 변환합니다.
제품 GUI/CLI와 회귀는 `app/`에 있습니다. 구형 Python 앱은 제거했습니다.

## 기본 빌드

```powershell
pwsh scripts/setup-dev.ps1
pwsh poc/ncnn-rife/bootstrap.ps1
pwsh poc/ncnn-rife/build.ps1
pwsh scripts/build.ps1
```

CMake/Ninja는 `F:/Temp/FrameDoubler/build-env`에 둡니다. 기본 빌드에 Torch/Qt가 필요하지 않습니다.
`bootstrap.ps1`은 SHA-256으로 고정한 ncnn 20261020·glslang·LLVM-MinGW를 외부 폴더에 준비합니다.
Vulkan SDK 없이 `NCNN_SIMPLEVK`와 정적 ncnn/glslang을 빌드합니다. CPU 대체도 지원합니다.
FFmpeg와 변환한 모델은 빌드 입력입니다. `package.py`는 엔진·변환 모델·고지를 준비하며
최종 `scripts/build.ps1`은 .NET 런타임을 포함한 `dist/FrameDoubler`와 ZIP을 만듭니다.

## 선택적 최초 모델 변환과 비교

`baseline/rife.py`·`baseline/weights.py`만 기존 MIT 구현에서 유지했습니다.
`export_model.py`의 모델 변환, `compare.py`의 PyTorch FP32 대조군에 실제 사용합니다.
`media_helpers.py`는 독립적인 RGB8 시험 입력 도구입니다. 제거한 `framedoubler` 패키지를 import하지 않습니다.
Python/Torch/pnnx·학습 가중치는 배포하지 않습니다. 내려받은 모델의 Python 코드는 실행하지 않으며
공식 가중치는 SHA-256 확인 후 `torch.load(weights_only=True)`로만 읽습니다.

기존 비교 환경은 `F:/Temp/FrameDoubler/comparison-env-20261114`, 가중치는 `ncnn-poc/weights`에 있습니다.
새 환경에는 `requirements-comparison.txt`와 장치에 맞는 공식 Torch 빌드를 설치합니다.

```powershell
$py='F:/Temp/FrameDoubler/comparison-env-20261114/Scripts/python.exe'
$work='F:/Temp/FrameDoubler/ncnn-poc'
foreach($model in 'v4.25','v4.26') {
    & $py poc/ncnn-rife/export_model.py --model $model --scale 1 --output "$work/models/$model-vulkan"
    & $py poc/ncnn-rife/export_model.py --model $model --scale 0.5 --output "$work/models/$model-vulkan-s05"
    & $py poc/ncnn-rife/compare.py --model $model --size 1920x1080 --repeats 30
}
& $py poc/ncnn-rife/stability.py --model v4.25 --iterations 480
```

비교 입력은 `samples/Fire Emblem Fortunes Weave 30fps 2.mp4`입니다. 실제 장치 측정은 순차 실행합니다.
엔진 `smoke.py`는 numpy만 필요하며 제품 변환·오디오·취소 검증은 C# 테스트를 사용합니다.
제거한 `video_compare.py`의 과거 결과는 N1 보고서에 보존했습니다.

## 엔진 계약

```text
rife-poc --hardware
rife-poc MODEL_DIR WIDTH HEIGHT fp16|fp32 auto|gpu:INDEX|cpu [SCALE] [rgb16] [sequence]
```

- RGB 0~1, 64/scale replicate padding, dense timestep, FP16 저장/FP32 연산. 4K scale=0.5.
- `rgb16`은 RGB48 little-endian이며 FP32 저장/연산. FP16 저장은 실제 장치 기능 확인 후 선택합니다.
- `P` + little-endian float32 timestep + RGB A/B 요청. 응답은 float64 시간 세 개 + RGB 중간 프레임.
  `Q`/EOF로 종료합니다. `sequence`는 연속 RGB30→RGB60 스트림이며 마지막 프레임을 유지합니다.
- scale별 param 두 개가 같은 bin을 공유합니다. 일반 ncnn GridSample의 CPU 대체를 피하려고
  독립 FDWarp CPU/Vulkan 커널을 사용합니다.
- 엔진 실행에 Python/Torch/.NET은 필요하지 않습니다. 제품 앱은 포함된 .NET 런타임을 사용합니다.
- 타 제조사 실제 장비, CUDA 대비 성능 목표, GPU zero-copy는 `PROGRESS.md`의 미완료 상태를 따릅니다.

모든 자체 코드는 MIT이며 패키지 `licenses/`에 외부 구성 요소 고지를 포함합니다.
