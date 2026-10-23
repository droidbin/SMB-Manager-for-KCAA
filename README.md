# SMB Manager

Windows SMB 공유폴더를 부서별 Drive로 연결하고 연결 상태 점검·자동 복구를 지원하는 **사내 업무용 프로그램**입니다. 개인·외부 기관·기업 대상 업데이트는 제공하지 않습니다.

| 구분 | 버전 | 다운로드 및 상태 |
|---|---|---|
| **정식** | **V1.7.11** | [정식 릴리스](https://github.com/droidbin/SMB-Manager-for-KCAA/releases/tag/V1.7.11) · [설치 ZIP](https://github.com/droidbin/SMB-Manager-for-KCAA/releases/download/V1.7.11/SMB.Manager.V1.7.11.zip) · [Latest](https://github.com/droidbin/SMB-Manager-for-KCAA/releases/latest) |
| **알파테스트** | **2.0.0-beta.5 · 클라이언트 빌드 25** | [시험용 릴리스](https://github.com/droidbin/SMB-Manager-for-KCAA/releases/tag/v2.0.0-beta.5-build25) · 전체 QA 미승인, 오픈베타 미승인 |

## 정식 설치

1. 정식 설치 ZIP을 다운로드하고 압축을 해제합니다.
2. `Setup.exe`를 실행해 설치를 완료합니다.
3. 생성된 바로가기로 프로그램을 실행합니다.

**포터블 실행은 지원하지 않습니다.** ZIP은 설치 파일을 담은 배포 패키지입니다. 정식 1.7.11은 반복 자동 재연결 문제를 수정한 핫픽스입니다.

## 알파테스트 안내

- 빌드 25는 클라이언트 전용 MSI입니다. 관리자/서버 설치본은 포함하지 않으며 기존 관리 서버와 실제 SMB 서버·배정 계정이 필요합니다.
- 1.x 앱의 **베타 버전 포함** 옵션은 **1.x 계열**에만 적용됩니다. 2.x 알파테스트는 해당 옵션으로 설치되지 않습니다.
- 설치 전 업무 파일을 저장하고 공유폴더의 문서·탐색기를 닫으세요. 설치 시 기존 SMB 연결·저장 매핑과 확인된 구버전을 정리하므로 릴리스의 안내를 먼저 확인하세요.
- 약 3시간 27분, 관찰 202회·복구 7회의 시험 후 사용자 요청으로 배포한 예외 후보입니다. 남아 있는 실제 설치 화면·Windows 11·재부팅·장기 사용·보안 프로그램 호환성 QA가 통과한 것으로 해석하지 않습니다.
- 빌드 25의 자동 **Source code** ZIP/TAR에는 1.x 소스만 있습니다. 배포 MSI를 재현하는 2.x 소스 스냅샷이 아닙니다. [소스 범위와 배포 이력](docs/DISTRIBUTION.md)을 확인하세요.

## 사용과 업데이트

- 부서별 폴더 연결, 저장 비밀번호, 자동 재연결, 창 닫기/완전 종료, 1.7.10 → 1.7.11 업데이트: [정식 1.x 사용 안내](docs/USER-GUIDE-1.x.md)
- 정식/알파테스트 구분, 과거 태그, QA 단계와 게시 원칙: [배포와 소스 이력](docs/DISTRIBUTION.md)
- 모든 설치파일과 체크섬: [GitHub Releases](https://github.com/droidbin/SMB-Manager-for-KCAA/releases)

## 소스와 빌드

| 경로 | 내용 |
|---|---|
| `src/legacy/` | 1.7.7~1.7.11 소스와 기존 문서·QA 기록 |
| `assets/fonts/` | 중복을 정리한 공유 폰트와 제3자 안내 |
| `tools/` | 소스 구성 검사와 기존 자산을 교체하지 않는 게시 도구 |
| `.local/legacy-builds/` | 로컬 빌드 출력, Git 제외 |

Windows에서 정식 소스를 빌드합니다.

```powershell
powershell -ExecutionPolicy Bypass -File ".\src\legacy\SMB Manager V1.7.11\source\build.ps1"
```

Actions는 빌드 산출물을 보관하며 일반 push에서는 릴리스를 게시하지 않습니다. 실제 게시에는 사전 검증한 패키지의 SHA256이 필요합니다. 빌드 성공은 실사용 QA 승인을 뜻하지 않습니다. 기존 정식 릴리스·태그·설치파일은 보존합니다.

이 저장소에 프로젝트 오픈소스 라이선스를 새로 부여하지 않았습니다. 제3자 자산의 안내는 해당 디렉터리에 있습니다.
