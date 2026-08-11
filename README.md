# TagSeeker

TagSeeker is a fast Windows image viewer and library manager designed for large image collections.
It combines thumbnail-based folder browsing, flexible tags, author filtering, multi-format image viewing, OCR, and translation in one desktop application.

## 주요 기능

- 대규모 폴더를 위한 페이지 기반 미리보기와 썸네일 캐시
- PNG, JPEG, WebP, GIF, TIFF, TGA 등 다양한 이미지 형식 지원
- 파일과 폴더에 여러 태그 적용 및 AND/OR 태그 모아보기
- 폴더명 앞의 `[작가]` 또는 `【작가】`를 이용한 작가 자동 필터
- 이름, 날짜, 형식, 크기 정렬과 폴더 우선 표시
- 탐색기 방식의 다중 선택, 앞/뒤 이동, 복사, 이동, 이름 변경 및 삭제
- 여러 이미지 맞춤 보기 모드와 키보드·마우스 탐색
- 이미지 문자 자동 인식, 언어 감지, 번역 및 번역 오버레이
- 마지막 폴더, 화면 위치와 사용자 설정 복원

## 다운로드 및 실행

1. [Releases](../../releases)에서 최신 `TagSeeker-*-win-x64-portable.zip`을 받습니다.
2. ZIP 파일을 원하는 폴더에 완전히 압축 해제합니다.
3. `TagSeeker.exe`를 실행합니다.

별도의 설치 과정이나 .NET 설치는 필요하지 않습니다. 현재 배포판은 Windows x64용입니다.

## 기본 조작

- `Enter` 또는 더블클릭: 선택한 폴더나 이미지 열기
- `Backspace`: 상위 폴더로 이동
- `Ctrl + 마우스 휠`: 미리보기 크기 조절
- `Ctrl+C / Ctrl+X / Ctrl+V`: 복사 / 잘라내기 / 붙여넣기
- `F2`: 이름 변경
- `Delete`: 휴지통으로 이동
- `Shift+Delete`: 즉시 삭제
- 이미지 보기에서 방향키 또는 마우스 휠: 이전·다음 이미지
- `Esc`: 미리보기 목록으로 돌아가기

## OCR 및 번역 안내

OCR은 최초 실행 시 준비 작업 때문에 시간이 걸릴 수 있습니다. 온라인 번역 방식을 선택하면 인식된 텍스트가 해당 번역 서비스로 전송될 수 있으며, 로컬 번역은 별도의 로컬 번역 환경이 준비된 경우 사용할 수 있습니다.

## 소스에서 빌드

- Windows 10/11
- Visual Studio 2022 이상 또는 .NET 10 SDK
- WPF 데스크톱 개발 도구

```powershell
dotnet build CustomImageViewer.csproj
```

자체 포함 Windows x64 배포 폴더는 다음 명령으로 만들 수 있습니다.

```powershell
.\build-release.ps1 -SkipInstaller
```

## 데이터 저장

태그, 설정, 번역 기록과 썸네일 캐시는 각 사용자의 로컬 애플리케이션 데이터 영역에 저장되며 배포 ZIP에는 포함되지 않습니다.
