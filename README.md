# SMB Manager

Windows SMB 공유폴더를 부서별로 자동 연결하고, 연결 상태 진단과 복구를 지원하는 관리 도구입니다.

※ 사내 업무용 프로그램으로, 개인 및 외부 기관, 기업을 대상으로 업데이트를 제공하지 않습니다.

## 다운로드

최신 설치 패키지는 GitHub Releases에서 받을 수 있습니다.

- 최신 릴리즈: <https://github.com/droidbin/smb-manager/releases/latest>
- 배포 파일: `SMB Manager Vx.x.x.zip`

zip 파일을 내려받아 압축을 해제한 뒤 `Setup.exe`를 실행하면 됩니다.

## 주요 기능

- 부서별 SMB 공유폴더 자동 연결
- 전체 연결 해제
- SMB 연결 진단 및 자동 복구
- 저장된 비밀번호를 사용한 진단 후 자동 재연결
- GitHub Releases 기반 업데이트 확인 및 자동 업데이트
- 1.7.10부터 새 버전 발견 시 시작 팝업 안내 및 앱 전용 아이콘
- 일반 설정에서 정식 버전만 또는 베타 버전 포함 업데이트 채널 선택
- SMB 연결 상태 주기 모니터링
- 일반 설정과 보안 설정 분리
- 앱 내부 관리자 인증 비밀번호 설정
- SMB 계정명 DPAPI 암호화 저장
- 최소화 또는 X 버튼 클릭 시 트레이 백그라운드 실행
- `Uninstall.exe`를 통한 프로그램 제거

## 설치 방법

1. Releases에서 최신 `SMB Manager Vx.x.x.zip` 파일을 다운로드합니다.
2. zip 파일을 압축 해제합니다.
3. 압축 해제된 폴더에서 `Setup.exe`를 실행합니다.
4. 설치 후 생성된 바로가기로 SMB Manager를 실행합니다.

## 업데이트 방식

앱은 실행 시 GitHub Releases의 최신 릴리즈를 확인합니다.

업데이트 조건:

- 최신 Release tag가 현재 앱 버전보다 높아야 합니다.
- Release asset에 `.zip` 파일이 포함되어야 합니다.
- zip 파일 안에 `Setup.exe`가 포함되어야 합니다.

새 버전이 있으면 zip을 다운로드하고 압축을 해제한 뒤 포함된 `Setup.exe`를 실행합니다.

기존 공유폴더 업데이트 방식의 구버전은 배포 폴더의 `latest.ini`와 버전 EXE를 `지점공용\App Update`에 복사해 전환할 수 있습니다. 구버전이 EXE를 교체한 뒤 새 앱이 내장 설치 프로그램을 실행하여 정식 설치 구조로 마이그레이션합니다.

브리지용 `latest.ini`는 정식 앱 버전과 같은 값을 사용합니다. 새 버전 배포 시 `latest.ini`와 같은 버전의 EXE 파일을 함께 복사합니다.

## 자동 릴리즈 업로드

이 저장소는 GitHub Actions를 사용해 배포 zip을 자동으로 GitHub Release에 업로드합니다.

동작 방식:

- `master` 브랜치에 `SMB Manager Vx.x.x.zip`이 push됩니다.
- `.github/workflows/release-package.yml` 워크플로가 실행됩니다.
- `version.ini`의 `Version` 값을 읽습니다.
- 같은 버전의 GitHub Release를 생성하거나 기존 Release asset을 갱신합니다.

## 보안 참고

- 앱 내부 관리자 비밀번호는 PBKDF2 해시로 저장합니다.
- SMB 계정명과 저장된 SMB 비밀번호는 Windows DPAPI CurrentUser 방식으로 암호화합니다.
- DPAPI로 저장된 값은 같은 Windows 사용자 계정에서만 복호화할 수 있습니다.
- 관리자 비밀번호 초기화 도구는 일반 배포 zip에 포함하지 않습니다.

## 개발 및 빌드

소스는 최신 버전 폴더의 `source` 디렉터리에 포함되어 있습니다.

빌드:

```powershell
powershell -ExecutionPolicy Bypass -File ".\SMB Manager V1.7.8\source\build.ps1"
```

빌드 결과:

- `SMB Manager Vx.x.x\SMB Manager Vx.x.x.exe`
- `SMB Manager Vx.x.x\Setup.exe`
- `SMB Manager Vx.x.x\Uninstall.exe`
- `SMB Manager Vx.x.x.zip`

## V1.7.8 변경 사항

- 유효한 설치 표식이 없는 압축 해제본은 포터블 실행하지 않고 설치 프로그램으로 전환

## 현재 버전

현재 최신 버전은 `V1.7.8`입니다.

주요 변경:

- 관리자 부서 연결 시 별도의 앱 내부 인증 단계 제거
- 최초 관리자 비밀번호는 `보안 설정` 진입 시에만 설정
- 최초 비밀번호 설정 직후 보안 설정 화면으로 바로 진입
- 재설치 전 실행 중인 SMB Manager를 종료해 잠긴 파일 교체 실패 방지
- 제거 전 실행 중인 앱을 종료해 설치 폴더 접근 거부 오류 방지
- 이전 제거 실패로 실행 파일만 남은 설치 경로도 다시 인식해 정리
- 공유폴더 구버전이 EXE만 교체해도 새 앱 첫 실행 시 정식 설치 프로그램으로 자동 전환
- Windows 시작 시 저장된 비밀번호로 업무폴더를 자동 재연결
- 설치 프로그램에서 시작프로그램 등록 옵션 기본 활성화
