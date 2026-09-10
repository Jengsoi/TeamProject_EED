# SafetyVision — 실행 README

산업안전 AI 보호구 착용 검사 시스템. WPF 클라이언트 + C# TCP 서버 + MySQL 구조 (패키지 v3.0 기준).
문서 원본: `docs/` 폴더 (00~07, README_전달방법.md, Claude_마스터프롬프트.md).

## 1. 사전 준비

- .NET 10 SDK (확인된 버전: 10.0.401)
- MySQL 8.x (개발 확인: MySQL Server 8.4.9 — winget `Oracle.MySQL` 카탈로그에 정확한 8.0.x가 없어 8.4 LTS로 진행. SQL 문법은 8.0과 사실상 호환되며, 서버는 `ServerVersion.AutoDetect`로 실제 버전을 인식한다.)
- Windows x64 (WPF 클라이언트 실행 환경)

## 2. 모델 준비

- 저장소: `ayushgupta7777/safetyvision-yolov8`, 버전 `v2`, 파일 `v2/best_640.onnx`
- 배치 경로: `src/SafetyVision.Server/models/safetyvision_v2_640.onnx` (서버 실행 디렉터리 기준 `models/`)
- **다운로드 날짜**: 2026-09-10
- **SHA-256**: `EA18AE903A566E8FA76F3EE1C503075522DCA269269315E9C862EFA170430B35`
- 모델 metadata 검증 결과: 입력명 `images`, 출력명 `output0`, 13개 클래스 중 사용하는 7개(Person/Hardhat/NO-Hardhat/Mask/NO-Mask/Safety Vest/NO-Safety Vest) 모두 이름 매칭 확인됨(서버 기동 로그로 확인, Day1).
- 모델 파일은 git에 커밋하지 않는다(`.gitignore`). 새 환경에서는 위 링크에서 다시 받아 같은 경로에 배치한다.

## 3. 데이터베이스 준비

```sql
CREATE DATABASE IF NOT EXISTS safetyvision CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;
CREATE USER IF NOT EXISTS 'safetyvision_app'@'localhost' IDENTIFIED BY '<비밀번호>';
GRANT ALL PRIVILEGES ON safetyvision.* TO 'safetyvision_app'@'localhost';
FLUSH PRIVILEGES;
```

연결 문자열은 `src/SafetyVision.Server/appsettings.json`의 `ConnectionStrings:MySql`에 설정한다.
**비밀번호는 이 문서에 원문으로 남기지 않는다** — 발표 환경 값은 팀 내 별도 공유.

마이그레이션 적용(최초 1회, 이후 서버가 기동 시 자동 적용):

```powershell
dotnet tool install --global dotnet-ef --version 9.0.20
$env:SAFETYVISION_MYSQL_CONNSTR = "Server=localhost;Port=3306;Database=safetyvision;User=safetyvision_app;Password=<비밀번호>;"
dotnet ef database update --project src/SafetyVision.Data --startup-project src/SafetyVision.Data
```

서버는 시작 시 자체적으로 `Database.MigrateAsync()`와 관리자 계정 Seed를 수행하므로, 위 수동 마이그레이션은 스키마를 미리 확인하고 싶을 때만 필요하다.
**서버 시작 시 MySQL 연결에 실패하면 신규 클라이언트 연결을 거부한다**(정상 동작, 04_DB설계.md 규격).

### MySQL을 Windows 서비스로 등록 (발표 PC, 관리자 권한 필요)

이 개발 환경에서는 관리자 권한이 없어 서비스 등록을 하지 못했고, `mysqld.exe`를 일반 프로세스로 직접 실행해 검증했다.
발표 PC에서는 관리자 PowerShell로 아래와 같이 서비스 등록을 권장한다:

```powershell
& "C:\Program Files\MySQL\MySQL Server 8.4\bin\mysqld.exe" --install MySQL84 --defaults-file="C:\ProgramData\MySQL\MySQL Server 8.4\my.ini"
Start-Service MySQL84
```

## 4. 초기 계정

- ID: `admin`
- 비밀번호: `SafetyVision!2026` (최초 DB 생성 시 서버가 1회 Seed, PBKDF2-HMAC-SHA256 600,000회 해시로 저장, 재실행 시 재설정하지 않음)

## 5. 실행

```powershell
# 서버
dotnet run --project src/SafetyVision.Server

# 클라이언트 (별도 터미널/PC)
dotnet run --project src/SafetyVision.Client
```

서버 기본 리슨 포트: `8910` (`appsettings.json`의 `ListenPort`).

## 6. 설정값과 근거

서버 설정 파일: `src/SafetyVision.Server/appsettings.json`. 05_AI모델명세.md 10절의 권장 초기값을 그대로 사용 중이며, 아직 발표 PC 실측(추론 속도·프레임 전송 지연)을 하지 못했다 — 값 변경 시 이유와 함께 이 절에 기록할 것.

| 항목 | 값 | 상태 |
|---|---|---|
| DetectionConfidence | 0.40 | 초기값, 미검증 |
| NmsIouThreshold | 0.45 | 초기값, 미검증 |
| TargetAnalysisFrames / MinAnalysisFrames | 12 / 5 | 초기값, 미검증 |
| MaxInferenceFps | 6 | 초기값, 미검증 |
| DecisionRatio | 0.70 | 문서 고정값 |

## 7. Day1 검증 결과 요약

- `dotnet build` (Client/Server/Core/Protocol/Data) 전체 0 오류.
- xUnit 35개 테스트 통과 (판정 로직 7개 예제, 상태 머신 시나리오, ROI/PPE 연결, TCP 프레이밍, PBKDF2).
- 실제 ONNX 모델 로드 및 metadata 검증 성공.
- 실제 MySQL 연결, 마이그레이션 적용, admin Seed 확인.
- 실제 TCP 클라이언트로 로그인 실패/성공, 대시보드 조회 End-to-End 확인.

## 8. 알려진 한계 (아직 실행 검증 필요)

- 실제 웹캠을 통한 검출 품질, 발표 PC 추론 속도, 클라이언트→서버 프레임 전송 지연은 실물 웹캠 환경에서 확인 필요.
- 다중 클라이언트 동시 접속 부하 테스트 미실시.
- MySQL Windows 서비스 등록은 발표 PC에서 관리자 권한으로 별도 진행 필요.
