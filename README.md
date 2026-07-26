# Zebra DS9908 Scanner Suite

Zebra **DS9908** 바코드 스캐너를 Windows PC에 연결해 사용하는 통합 프로그램입니다.
바코드 리딩, 이미지 캡처, OCR 입력, ISO/IEC 15415 기반 검증 시뮬레이션, 연속(Multi) 스캔을 지원합니다.

| 탭 | 기능 |
|---|---|
| **일반 스캔** | ① 바코드만 / ② 바코드+이미지 캡처 / ③ 바코드+이미지+OCR 입력 · 저장경로/파일명규칙 · 영역선택 OCR · GS1 데이터 추출 |
| **BARCODE VERIFY** | ISO/IEC 15415 기반 품질 등급 시뮬레이션 + HTML 리포트 저장/출력 |
| **Multi / Continuous** | 트리거 유지 상태로 시야 내 바코드를 연속·고속 리딩 (중복 자동 제거, CSV 내보내기) |

---

## 1. 설치 (사용자 PC 준비)

복잡한 설정 없이 아래 2가지만 설치하면 됩니다.

1. **Zebra Scanner SDK for Windows** (CoreScanner 드라이버 포함) — Zebra 공식 배포 키트
   - 다운로드: <https://www.zebra.com/us/en/support-downloads/software/developer-tools/scanner-sdk-for-windows.html>
   - 설치 시 기본 옵션(CoreScanner Driver 포함)으로 설치
2. **.NET 8 Desktop Runtime** — <https://dotnet.microsoft.com/download/dotnet/8.0>

이후 DS9908을 USB로 연결하고 본 프로그램(`ZebraScannerSuite.exe`)을 실행하면 자동으로 스캐너를 검색·연결합니다.

> 선택 사항: 스캐너 상세 설정(심볼로지 활성화, 프레젠테이션 모드 등)이 필요하면 Zebra **123Scan** 유틸리티를 함께 사용할 수 있습니다.

### 호스트 모드 (HID / SNAPI 자동 전환)

- 이미지 캡처·검증 기능은 **USB SNAPI (이미징 지원)** 모드가 필요합니다.
- 프로그램 상단의 **호스트 모드** 콤보에서 선택 후 **[모드 전환]** 을 누르면 프로그램이 스캐너를 자동 전환합니다
  (SNAPI ↔ HID 키보드 ↔ IBM 핸드헬드 등, "영구 적용" 체크 시 전원 재인가 후에도 유지).
- 스캐너가 HID 키보드 모드인 경우에도 "HID 키보드 입력" 칸에 포커스를 두고 스캔하면 동일하게 처리됩니다(이미지 기능 제외).

---

## 2. 빌드 (개발자)

Windows + **.NET 8 SDK**만 있으면 됩니다. CoreScanner 연동은 수동 COM interop
(`Services/CoreScannerInterop.cs`)으로 구현되어 있어 빌드 시 Zebra SDK나 Visual Studio가 필요 없습니다.

```powershell
dotnet build ZebraScannerSuite.sln -c Release
```

생성 위치: `src\ZebraScannerSuite\bin\Release\net8.0-windows10.0.19041.0\ZebraScannerSuite.exe`

```powershell
# 다른 PC 배포용
dotnet publish src/ZebraScannerSuite -c Release -r win-x64 --self-contained false
```

> - 실행 시 COM 등록 오류(0x80040154, "클래스가 등록되지 않았습니다")가 나면: 실행 PC에 Zebra Scanner SDK가 설치되어 있는지 확인하고, 그래도 같으면 `csproj`의 `PlatformTarget`을 `x86`으로 바꿔(32비트 SDK 설치 시) 다시 빌드하세요.
> - `error MSB4803: ResolveComReference ...` 오류는 구버전 소스(COMReference 방식)에서만 발생합니다. 최신 소스를 받으세요.

---

## 3. 기능 상세

### 탭 1 — 일반 스캔

- **동작 모드**: ① 바코드만 리딩 / ② 바코드 + 이미지 캡처 / ③ 바코드 + 이미지 + OCR 입력
- 트리거를 당겨 바코드를 읽으면(②/③ 모드) 같은 위치의 이미지를 자동으로 캡처·저장하고 **UI에 즉시 표시**합니다.
- **이미지 저장 경로**: 설정 후 프로그램을 재실행해도 유지됩니다
  (설정 파일: `%APPDATA%\ZebraScannerSuite\settings.json`).
- **파일명 규칙**: 토큰 조합으로 자유 설정. 중복 시 일련번호 자동 증가.
  - `{DATE:yyyyMMdd}_{BARCODE}_{SEQ:3}` → `20260726_8801234567890_001.jpg`
  - 사용 가능 토큰: `{DATE[:형식]}` `{TIME[:형식]}` `{BARCODE}` `{SYMBOLOGY}` `{SEQ[:자릿수]}` `{OCR}` (`###` = `{SEQ:3}`)
- **OCR**:
  - Windows 10/11 **내장 OCR** 사용(별도 설치 불필요, 고속).
  - **허용 패턴(정규식)** 에 일치하는 문자만 채택하고 나머지는 무시합니다. 예: `\d{4}-\d{2}-\d{2}` (날짜), `\b[A-Z0-9]{8}\b` (LOT 8자리)
  - 캡처된 사진 위에서 **마우스로 영역을 드래그 → [선택 영역 OCR]** 로 원하는 글자만 인식시킬 수 있습니다.
  - 인식 값은 OCR 필드에 입력되고 옵션에 따라 클립보드로 복사되어 타 프로그램에 빠르게 붙여넣을 수 있습니다.
- **데이터 추출 규칙**: 획득된 바코드에서 원하는 정보만 별도 필드에 표시.
  - `GS1` — 응용식별자(AI) 지정: `01`(GTIN/품번), `10`(LOT), `11`(제조일), `17`(유효기한), `21`(시리얼) 등. 날짜변환 체크 시 `YYMMDD → YYYY-MM-DD`
  - `REGEX` — 정규식(캡처그룹 1 우선)
  - `SUBSTR` — 시작 위치(1부터) + 길이로 고정 위치 추출
- **상태 표시**: 이미지 저장·OCR 진행 상태를 상태바의 PROGRESS BAR와 "버퍼 N건" 표시로 확인할 수 있습니다.
  처리 파이프라인이 백그라운드에서 동작하므로 연속 입력에도 UI가 밀리지 않습니다.

### 탭 2 — BARCODE VERIFY (ISO/IEC 15415 시뮬레이션)

전용 검증기 하드웨어 없이 DS9908의 이미저 캡처와 알고리즘 로직으로 **가능한 범위까지** ISO/IEC 15415 파라미터를 근사 산출합니다.

| 파라미터 | 산출 방법 |
|---|---|
| Decode | 캡처 이미지 디코드 성공 여부 (ZXing) |
| Symbol Contrast (SC) | 심볼 영역 반사율 히스토그램 Rmin/Rmax |
| Modulation (MOD) | 모듈 격자 샘플링 기반 추정 |
| Axial Nonuniformity (AN) | X/Y 모듈 피치 비교 |
| Grid Nonuniformity (GN) | 국부(사분면) 피치 편차 근사 |
| Unused Error Correction (UEC) | 디코더 오류정정 통계 기반 근사 |
| Fixed Pattern Damage (FPD) | QR 파인더 / DataMatrix L-파인더·클록트랙 샘플링 |

- 종합 등급 = 파라미터 중 **최저 등급** (ISO 15415 방식, A~F / 4.0~0.0)
- 측정 결과는 세션에 누적되며 **[리포트 저장]** 으로 캡처 이미지 포함 **HTML 리포트**(+개별 PNG)로 저장됩니다.
  브라우저에서 열어 인쇄하거나 PDF로 저장할 수 있습니다.

> **고지**: ISO 15415는 교정된 조명(45°/0°)·개구·반사율 기준을 요구합니다. 본 결과는 공식 성적이 아닌
> **대략적인 경향 파악(사전 준비)용 시뮬레이션**입니다.

### 탭 3 — Multi / Continuous

- **[시작]** 을 누르면 트리거를 당긴 상태(SDK 트리거 유지 + 자동 재트리거)로 시야에 들어오는 바코드를
  최대한 빠르게 연속 리딩합니다.
- **중복은 자동 제거**되고 읽은 횟수만 집계됩니다. 여러 바코드를 빠르게 훑으며 획득하는 용도.
- 결과는 CSV로 내보낼 수 있습니다.
- DS9908 프레젠테이션(핸즈프리) 모드에서는 상시 감지되므로 그대로 리스트에 수집됩니다.

---

## 4. 문제 해결

| 증상 | 조치 |
|---|---|
| "CoreScanner 초기화 실패" / COM 오류 | Zebra Scanner SDK(CoreScanner 드라이버) 설치 확인, 서비스 실행 확인 |
| 바코드는 읽히는데 이미지가 안 옴 | 호스트 모드를 **USB SNAPI (이미징 지원)** 으로 전환 |
| 스캐너 미검색 | USB 재연결 → [스캐너 새로고침]. HID 키보드 모드면 SDK가 제어할 수 없으므로 123Scan 또는 설정 바코드로 SNAPI 전환 |
| OCR 결과 없음 | Windows 언어 설정에 OCR 언어팩 추가(설정>시간 및 언어>언어), 허용 패턴 확인 |
| 이미지가 흐림 | 심볼과의 거리 조정, 조명 개선 (검증 정확도에 직접 영향) |

## 5. 프로젝트 구조

```
src/ZebraScannerSuite/
 ├─ MainWindow.xaml(.cs)      # 3-탭 UI 및 전체 워크플로우
 ├─ Services/
 │   ├─ CoreScannerService.cs # Zebra CoreScanner COM 연동 (이벤트/트리거/캡처/모드전환)
 │   ├─ OcrService.cs         # Windows 내장 OCR + 패턴 필터
 │   ├─ Iso15415Verifier.cs   # ISO 15415 시뮬레이션 등급 산출
 │   ├─ ReportService.cs      # HTML 검증 리포트
 │   ├─ Gs1Parser.cs          # GS1 응용식별자 파서
 │   ├─ DataExtractionService.cs
 │   ├─ ImageSaveService.cs   # 파일명 규칙/일련번호
 │   └─ SettingsService.cs    # 설정 영구 저장
 └─ Models/                   # 설정·스캔·검증 모델
```
