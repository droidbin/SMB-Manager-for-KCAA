# SMB Manager V2.0.5 패치 및 운영 인수인계 문서

> 역사 기록: 아래 .146 버전 증가, open-beta의 정식 표시, 완료 플래그 수동 변경 가이드는 후속 검토에서 대체됐습니다. 현재 수정 내용과 실제 검증/실패 이력은 [패치 검토 기록](PATCH-2.0.5-REVIEW-2026-11-04.md)을 따르세요. 이 문서 자체는 배포 승인 근거가 아닙니다.

본 문서는 Windows 10 환경에서 발생한 설치 완료 반복 오류 해결 및 2.0 정식 버전 명칭 정돈(업데이트 채널 2종 이원화)에 관한 기술 배경, 코드 수정 내역, 빌드·패키징 절차 및 현장 조치 가이드를 기록한 인수인계 문서입니다.

---

## 1. 이슈 개요

| 구분 | 주요 현상 | 원인 분류 |
|---|---|---|
| **이슈 1** | Windows 10 일부 PC에서 "설치가 완료되지 않았다"는 오류가 출력됨. '설치 마무리 다시 시도'를 누르면 정상 사용 가능하나, 재부팅 시 동일한 증상이 매번 재발함. | `InstallCompletion.IsInteractiveUserSession()`의 로컬 관리자 권한 배제 버그 및 상태 저장 로직 결함 |
| **이슈 2** | 2.0 정식 버전 설치 후에도 바탕화면 바로가기에 "클로즈베타 클라이언트", 업데이트 다이얼로그에 "공개 시험 · 오픈베타", "사내 시험 · 알파테스트" 등의 구버전 문구가 잔존함. | MSI WiX 패키징 스크립트 바로가기 명칭 하드코딩 및 `Protocol.cs` 채널 매핑 미정돈 |
| **이슈 3** | 재패키징된 MSI 설치 시 기존 구버전 바이너리가 덮어씌워지지 않고 건너뛰어지는 현상. | Windows Installer (msiexec) MajorUpgrade 규칙: 동일 버전(`2.0.145`) Skip 현상 |

---

## 2. 세부 원인 분석 및 코드 수정 내역

### 2.1. 이슈 1: Windows 10 설치 마무리 무한 반복 오류

#### 🔍 근본 원인
1. **관리자 권한 계정의 대화형 세션 배제**:
   - `next/client-2.0/ClientEngine/InstallCompletion.cs`의 `IsInteractiveUserSession()`에 아래의 조건이 존재했습니다:
     ```csharp
     public static bool IsInteractiveUserSession() => OperatingSystem.IsWindows()
         && System.Diagnostics.Process.GetCurrentProcess().SessionId != 0
         && !new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
     ```
   - 원래 의도는 MSI 인스톨러가 백그라운드 관리자 권한(Elevated/SYSTEM)으로 실행될 때 이를 데스크톱 일반 세션 완료로 오인하지 않도록 하는 것이었습니다.
   - 그러나 실제 사내 Windows 10 PC 중 **로컬 Administrator 그룹에 속한 계정**이나 **UAC가 비활성화된 PC**에서는 데스크톱 GUI 세션 자체가 `IsInRole(Administrator) == true`입니다.
   - 이로 인해 해당 PC에서는 `IsInteractiveUserSession()`이 **영원히 `false`를 반환**했습니다.

2. **무한 재발 메커니즘**:
   - `ClientForm.InitializeClient`에서 `install-completion.json`의 `UserSessionCompleted == false`를 감지하고 설치 마무리 미완료 화면(`installPending = true`)을 표시합니다.
   - 사용자가 '설치 마무리 다시 시도'를 누르면 임시로 초기화 루프를 통과하여 **당장은 사용이 가능**합니다.
   - 그러나 이때 파일에 다시 저장되는 결과 역시 관리자 계정이므로 여전히 `"UserSessionCompleted": false`로 기록됩니다.
   - **PC를 재부팅하면** 앱이 다시 실행되면서 `UserSessionCompleted: false`를 읽고 동일한 검사에 걸려 **오류가 매 부팅마다 무한 반복**되었습니다.

#### 🛠️ 수정 코드
- **[`InstallCompletion.cs`](file:///c:/Users/Admin/Documents/Gemini_Project/SMB-Manager-for-KCAA/next/client-2.0/ClientEngine/InstallCompletion.cs)**:
  ```csharp
  // 대화형 데스크톱 세션(SessionId != 0, UserInteractive) 및 유효한 사용자 SID 검증
  public static bool IsInteractiveUserSession()
  {
      if (!OperatingSystem.IsWindows() || !Environment.UserInteractive) return false;
      if (System.Diagnostics.Process.GetCurrentProcess().SessionId == 0) return false;
      var sid = WindowsIdentity.GetCurrent().User?.Value ?? "";
      return sid is not ("" or "S-1-5-18" or "S-1-5-19" or "S-1-5-20");
  }
  ```
- **[`ClientForm.cs`](file:///c:/Users/Admin/Documents/Gemini_Project/SMB-Manager-for-KCAA/next/client-2.0/ClientDesktop/ClientForm.cs)**:
  - `RetryInstallCompletion()` 및 `InitializeClient()`에서 설치 마무리 성공 시 `UserSessionCompleted = true`를 파일에 확실히 보존하도록 처리하여 재부팅 후 재검사 루프를 차단했습니다.

---

### 2.2. 이슈 2: 업데이트 채널 이원화 및 정식 명칭 통일

#### 🔍 근본 원인
- `Contracts/Protocol.cs`와 `ClientDesktop/BetaDialog.cs`에 과거 테스트 단계에서 사용하던 `알파테스트`, `오픈베타`, `사내 시험`, `공개 시험` 등의 문구가 남아 있었습니다.
- 실제 운영 서버에서는 `open-beta` 키에 2.0.5 정식 안정화 버전이 배포되어 있었으며, `closed-beta`는 내부 시험용으로 운영되고 있었습니다.

#### 🛠️ 수정 코드
- **[`Protocol.cs`](file:///c:/Users/Admin/Documents/Gemini_Project/SMB-Manager-for-KCAA/next/client-2.0/Contracts/Protocol.cs)**:
  ```csharp
  public static class BetaChannels
  {
      public const string Open = "open-beta", Closed = "closed-beta";
      public static bool Valid(string? channel) => channel is Open or Closed;
      public static string Label(string channel) => channel == Open ? "정식" : "베타";
  }
  ```
- **[`BetaDialog.cs`](file:///c:/Users/Admin/Documents/Gemini_Project/SMB-Manager-for-KCAA/next/client-2.0/ClientDesktop/BetaDialog.cs)**:
  - 드롭다운 콤보박스에 수식어를 제거하고 **`정식`**, **`베타`** 2개만 직관적으로 표시되도록 단일화.
  - 드롭다운 목록 순서를 '정식'이 최상단에 오도록 정렬 (`access.Allowed.OrderBy(c => c == BetaChannels.Open ? 0 : 1)`).
- **[`LegacyMigration.cs`](file:///c:/Users/Admin/Documents/Gemini_Project/SMB-Manager-for-KCAA/next/client-2.0/ClientEngine/LegacyMigration.cs)**:
  - 구버전 바로가기 정리 대상에 기존 `SMB Manager 클로즈베타 클라이언트.lnk` 및 시작메뉴 `SMB Manager Client Beta` 폴더를 추가하여, 설치 및 마이그레이션 시 자동 정리되도록 개선.

---

### 2.3. 이슈 3: MSI 업그레이드 규칙 (MajorUpgrade) 대응

#### 🔍 근본 원인
- Windows Installer (msiexec)는 대상 시스템에 설치된 버전과 신규 MSI의 `Version` 및 파일의 `FileVersion`이 동일(`2.0.145`)할 경우 파일 교체를 생략합니다.

#### 🛠️ 조치 사항
1. `Directory.Build.props`:
   - `FileVersion`: `2.0.146.0`
   - `InformationalVersion`: `2.0.5+qa.38`
2. `tools/Package-Client-2.0.5-Patch.ps1`:
   - WiX 패키지 Product Version: `2.0.146`
   - MajorUpgrade 설정: `<MajorUpgrade Schedule="afterInstallInitialize" DowngradeErrorMessage="..."/>`
   - 바로가기 생성 명칭: `SMB Manager` (바탕화면 및 시작메뉴 모두 통일)
   - 디지털 서명: KCAA 사내 인증서 및 타임스탬프 자동 서명 파이프라인 적용.

---

## 3. 패키징 및 산출물 명세

- **빌드 스크립트 위치**: [`tools/Package-Client-2.0.5-Patch.ps1`](file:///c:/Users/Admin/Documents/Gemini_Project/SMB-Manager-for-KCAA/tools/Package-Client-2.0.5-Patch.ps1)
- **최종 산출물**:
  - 파일 경로: `dist/SMB-Manager-Client-2.0.5-Patch-x64.msi`
  - 파일 크기: 89,571,328 bytes (약 85.4 MB)
  - SHA-256: `9E571DCA2028731A6DF870247731C9E8F688A04FFEDD56842230A143E4521415`
  - 서명 상태: KCAA 코드 서명 인증서 서명 완료

### 재빌드 실행 방법 (PowerShell)
```powershell
powershell -ExecutionPolicy Bypass -File .\tools\Package-Client-2.0.5-Patch.ps1
```

---

## 4. 현장 장애 발생 시 긴급 조치 가이드

신규 MSI를 배포하기 전, 현장에서 재부팅 오류를 겪고 있는 특정 PC에 즉시 적용 가능한 1분 조치 방법입니다:

1. **설치 완료 상태 강제 갱신**:
   - 실행 창(`Win + R`)에서 아래 경로 입력:
     ```text
     %LocalAppData%\SMB Manager Internal Beta 1\client
     ```
   - `install-completion.json` 파일을 메모장으로 열고, `"userSessionCompleted": false` 값을 **`"userSessionCompleted": true`** 로 수정한 후 저장합니다.
   - 이후 PC를 재부팅해도 오류 창이 뜨지 않고 정상 백그라운드 구동됩니다.

2. **아이콘 이름 정돈**:
   - 바탕화면의 `SMB Manager 클로즈베타 클라이언트` 바로가기를 선택하고 `F2`를 눌러 `SMB Manager`로 이름을 변경합니다.
