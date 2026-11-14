# 선택형 ONNX Runtime RIFE 엔진

독립 C++ 실행 파일 `rife-ort.exe`는 기본 ncnn 엔진과 같은 `P` + float32 시간값 + 두 RGB8 프레임 요청과 세 float64 시간값 + 결과 프레임 응답을 사용합니다. Python을 제품에서 실행하지 않습니다. FP32 네트워크와 warp, 원본 해상도를 유지합니다.

앱의 선택형 가속 기능으로 사용합니다. `--probe`와 실제 세션 출력의 EP 배정을 확인하고 CPU EP 대체를 금지합니다. 기본 경로만 시험하려면 `-SkipAcceleration`으로 빌드해 이 엔진과 가속 모델을 제외할 수 있습니다.

빌드 입력은 [공식 ONNX Runtime 1.32.0 C API 헤더](https://github.com/microsoft/onnxruntime/tree/fab30a46491a40f5e4c968b80e8a59acbbb91b53/include/onnxruntime/core/session)와 기존 LLVM-MinGW 컴파일러입니다. `bootstrap.ps1 -DownloadHeaders`를 명시적으로 실행하면 고정 커밋의 세 헤더를 받고 SHA-256을 확인합니다. 헤더는 `tools/dev/onnx/include`에 둡니다. `build.ps1`은 자동 다운로드하지 않고 고정 SHA만 확인하며 ORT import library나 CUDA SDK를 요구하지 않습니다. ONNX Runtime 인터페이스의 MIT 고지는 `ONNXRuntime-LICENSE.txt`입니다.

FP32 RGB8 그래프는 `poc/ncnn-rife/export_onnx.py`와 `convert_onnx_rgb8.py`로 두 모델×두 scale을 만든 뒤 `package_models.py`로 패키징합니다. 모델별 두 그래프는 같은 외부 가중치 파일을 공유합니다. 그래프·가중치는 기본 앱의 `models/onnx`에 넣고, 선택형 런타임 DLL은 기본 패키지에 넣지 않습니다.

추가 런타임은 사용자가 설치할 때 공식 PyPI의 고정 SHA-256 휠에서 DLL과 고지만 추출해 `accel/rtx`에 둡니다. ONNX Runtime은 MIT, 플러그인은 Apache-2.0, 포함 NVIDIA 런타임은 별도 [TensorRT RTX SLA](https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/reference/sla.html)를 따릅니다. 설치는 실제 EP 장치 조회·CPU 대체 금지 추론·ncnn CPU 수치 대조까지 성공해야 확정합니다.

장치 번호는 ORT 장치와 Vulkan 장치의 vendor/device ID가 유일하게 대응할 때만 선택합니다. 동일 ID의 복수 장치를 추측해 연결하지 않습니다. RGB16은 ncnn FP32를 사용합니다. RTX 실패는 같은 요청을 ncnn으로 재시도하며, ncnn의 기존 CPU 대체 정책은 유지합니다. 업데이트는 `accel`을 새 앱으로 이동하고, 부팅 실패 때 원래 앱으로 되돌립니다.

```text
rife-ort --probe RUNTIME_DIRECTORY
rife-ort MODEL.onnx WIDTH HEIGHT fp32 gpu:INDEX SCALE RUNTIME_DIRECTORY CACHE_DIRECTORY
```

실제 EP 장치 선택은 [ORT 플러그인 API](https://onnxruntime.ai/docs/execution-providers/plugin-ep-libraries/usage.html)를 사용하며 세션에서 CPU EP 대체를 금지합니다. 실행 프로파일은 패딩한 입력 크기에 고정하고, TF32를 끄며, 형상별 런타임 캐시를 구분합니다. 동시 실행 수는 기본 Vulkan 측정치를 전용하지 않습니다.
