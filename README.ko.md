# FrameDoubler

프레임 사이를, 움직임으로.

30fps 게임 녹화를 RIFE로 보간하는 Windows 프로그램입니다. 59.94/60fps 파일에
반복 저장된 프레임을 판별한 뒤 실제 움직임 사이에 새 프레임을 만듭니다.
[Practical-RIFE](https://github.com/hzwer/Practical-RIFE)의 공개 가중치와 PyTorch/CUDA를
사용하며 분석·타임라인·변환 파이프라인·GUI는 독립적으로 개발했습니다.

## 로컬 실행 패키지

로컬 후보 패키지의 ZIP을 쓰기 가능한 폴더에 전체 압축 해제하고
`FrameDoubler.exe`를 실행합니다. `_internal`, `tools`, `licenses` 폴더도 함께
유지해야 합니다. Python 설치가 필요 없도록 구성했으며 **별도 Windows 11 PC의
최종 검증은 아직 대기 중**입니다. 현재 공개 릴리스는 없습니다.

Windows 11 x64와 CUDA 13.0 런타임을 지원하는 NVIDIA 드라이버/GPU가 필요합니다.
개발·성능 검증 환경은 RTX 4080 SUPER 16GB입니다. 다른 GPU의 호환성이나 처리
속도는 확인하지 않았습니다. 모델은 첫 변환 때 내려받아
`%LOCALAPPDATA%\FrameDoubler\models`에 저장하며 첫 사용에는 인터넷 연결이 필요합니다.

영상을 추가하거나 끌어 놓으면 파일 fps와 추정 움직임 fps를 표시합니다.
YouTube 프리셋으로 변환을 시작하면 대기열을 순서대로 처리하고 결과를 검증합니다.
출력은 선택한 폴더의 `<원본 이름>_smooth.mp4`입니다. 설정에서 한국어/영어,
출력 폴더, 프리셋을 바꿀 수 있고 마지막 선택은 재시작 후 복원됩니다.
기존 파일을 바꾸려면 덮어쓰기를 명시적으로 선택해야 합니다. 취소하면 현재
미완료 파일을 지우고 이미 완료한 파일은 유지합니다.

GUI는 59.94/60/119.88/120fps를 제공합니다. YouTube 프리셋은 60fps까지 지원하며
고프레임 출력에서는 기본 HEVC로 전환합니다. HDR은 지원하지 않습니다. 실제
10비트 SDR 검증과 컷·페이드·어두운 장면·프레임 드롭이 포함된 추가 녹화 검증은
대기 중입니다. 결과 검증은 시간·오디오·중복의 휴리스틱 검사이며 육안 품질을
보증하지 않습니다.

## 명령줄

패키지에서는 PowerShell에서 `FrameDoubler-cli.exe`를 사용합니다.

```powershell
.\FrameDoubler-cli.exe analyze input.mp4
.\FrameDoubler-cli.exe run -i input.mp4 --preset youtube --verify
.\FrameDoubler-cli.exe batch "D:\Captures" -o "D:\Converted" --recursive --verify
.\FrameDoubler-cli.exe run -i input.mp4 --fps 120 --verify
.\FrameDoubler-cli.exe verify input.mp4 input_120fps.mp4 --fps 120
```

기본 인코딩은 HEVC CQ 19입니다. `--codec h264|hevc|av1`, `--quality 1~51`
(낮을수록 선명), `--bitrate 24M`을 선택할 수 있습니다. `--profile`은 단계별 시간을,
`--serial`은 진단용 직렬 처리를 사용합니다. 일괄 처리의 기본 출력 폴더는 입력
아래 `FrameDoubler`이며 오류 후 계속 처리합니다. `--stop-on-error`로 중단합니다.

## 개발 환경

Python 3.14와 PowerShell 7에서 다음을 실행합니다.

```powershell
pwsh scripts\setup.ps1
.venv\Scripts\python -m pytest -q
.venv\Scripts\python -m framedoubler gui
pwsh scripts\build.ps1
```

개발 환경의 모델 경로는 저장소의 `models`이며 `FRAMEDOUBLER_MODELS`로 바꿀 수
있습니다. FFmpeg는 `tools\ffmpeg\bin` 또는 `FRAMEDOUBLER_FFMPEG_DIR`에서 찾습니다.
설정은 `%APPDATA%\FrameDoubler\settings.json`에 저장하며
`FRAMEDOUBLER_CONFIG_DIR`로 격리할 수 있습니다.

자세한 옵션은 [영어 안내](README.md), 패키지 검증과 라이선스 준비 상태는
[배포 안내](docs/distribution.md), 진행 상황은 [todo.md](todo.md)에 있습니다.
FrameDoubler는 MIT이며 외부 구성 요소는 [별도 고지](THIRD_PARTY_NOTICES.md)를 따릅니다.
