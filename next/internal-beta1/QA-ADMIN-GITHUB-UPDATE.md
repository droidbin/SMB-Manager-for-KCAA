# 관리자 GitHub 업데이트 기능

## 구현 범위
- 관리자 화면의 업데이트 확인 버튼. 회사 관리 서버 통신 장애 중에도 열 수 있음.
- 공개 저장소 droidbin/SMB-Manager-for-KCAA의 GitHub Release API에서 관리자용 암호 ZIP만 선택.
- 베타 포함 선택 가능. 버전과 빌드 번호를 숫자로 비교하며 1.7 및 직원 설치 파일을 제외.
- GitHub SHA-256 digest와 다운로드 크기 검증. 허용한 HTTPS GitHub 다운로드 호스트만 이용.
- 검증 실패·취소 시 임시 파일 정리. 기존 파일 덮어쓰기 금지. 자동 설치·관리 서버 종료는 수행하지 않음.
- 다운로드 후 별도 암호로 압축 해제하고 업무 종료 후 관리자 서버 완전 종료 → MSI 설치 → 관리자 재실행 안내.
- ZIP 암호·Drive 암호·관리 API 키를 내장하거나 GitHub 요청에 첨부하지 않음.

## 검증
- 전체 솔루션 빌드 경고 0 / 오류 0.
- 업데이트 전용 격리 검사 17개 통과: 숫자 버전 정렬, 정식/베타 분리, 잘못된 파일·해시·리다이렉트 거절, 취소 정리, 기존 파일 보존 등.
- 실제 GitHub API 조회 및 현재 관리자 ZIP 다운로드·크기·SHA-256 검증 통과. 검사 다운로드 파일은 정리.
- 기존 격리 통합 QA 148개 통과: .local/qa/20261021-143515-f04874/report.json.
- 실제 Windows 세션의 업데이트 창 기본·최소 크기 검사 2개 통과. 최소 크기 렌더링 육안 확인.
- 상세 검사: .local/admin-update-qa/evidence/report.json.
- 화면 검사: .local/admin-github-update-build/ui-result.json.

## 배포 상태
코드와 별도 시험 빌드에 반영. 공개된 beta.2 빌드 10 파일은 수정하지 않았으며 현재 설치된 관리자 앱도 교체하지 않음. 이 기능을 처음 사용하는 경우 새 관리자 설치본을 한 번 수동 설치해야 함. 이후 앱에서 GitHub 릴리즈 확인 및 설치 ZIP 저장 가능. ZIP 해제·MSI 실행은 수동 단계로 남음.

참고: GitHub Release API의 asset digest 및 download URL 형식 https://docs.github.com/en/rest/releases/releases
