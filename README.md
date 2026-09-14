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

`src/SafetyVision.Server/appsettings.json`에는 비밀번호를 저장하지 않는다. 서버 실행 전에 `ConnectionStrings__MySql` 환경 변수로 전체 연결 문자열을 주입한다.
**비밀번호는 이 문서에 원문으로 남기지 않는다** — 발표 환경 값은 팀 내 별도 공유.

마이그레이션 적용(최초 1회, 이후 서버가 기동 시 자동 적용):

```powershell
dotnet tool install --global dotnet-ef --version 9.0.20
$env:SAFETYVISION_MYSQL_CONNSTR = "Server=localhost;Port=3306;Database=safetyvision;User=safetyvision_app;Password=<비밀번호>;SslMode=None;AllowPublicKeyRetrieval=True;"
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
- 비밀번호: 프로젝트 기본값은 `SafetyVision!2026`이며, 최초 배포 전 `SAFETYVISION_ADMIN_INITIAL_PASSWORD` 환경 변수로 일회성 운영 비밀번호를 지정한다. 서버는 최초 생성 시 1회만 Seed하고 PBKDF2-HMAC-SHA256 600,000회 해시로 저장하며, 재실행 시 재설정하지 않는다.

## 5. 실행

```powershell
# 서버
$env:ConnectionStrings__MySql = "Server=localhost;Port=3306;Database=safetyvision;User=safetyvision_app;Password=<비밀번호>;SslMode=None;AllowPublicKeyRetrieval=True;"
$env:SAFETYVISION_ADMIN_INITIAL_PASSWORD = "<최초 관리자 비밀번호>"
dotnet run --project src/SafetyVision.Server

# 클라이언트 (별도 터미널/PC)
dotnet run --project src/SafetyVision.Client
```

서버 기본 리슨 주소/포트: `127.0.0.1:8910` (`appsettings.json`의 `ListenAddress`/`ListenPort`). 다른 PC에서 접속할 때만 사설망 IP로 `ListenAddress`를 명시하고 TCP 구간은 TLS 또는 VPN으로 보호한다.

## 6. 설정값과 근거

서버 설정 파일: `src/SafetyVision.Server/appsettings.json`. 05_AI모델명세.md 10절의 권장 초기값을 그대로 사용 중이며, 아직 발표 PC 실측(추론 속도·프레임 전송 지연)을 하지 못했다 — 값 변경 시 이유와 함께 이 절에 기록할 것.

| 항목 | 값 | 상태 |
|---|---|---|
| DetectionConfidence | 0.40 | 초기값, 미검증 |
| NmsIouThreshold | 0.45 | 초기값, 미검증 |
| TargetAnalysisFrames / MinAnalysisFrames | 12 / 5 | 초기값, 미검증 |
| MaxInferenceFps | 6 | 초기값, 미검증 |
| DecisionRatio | 0.70 | 문서 고정값 |

## 7. 테스트 실행

```powershell
dotnet test tests/SafetyVision.Tests
```

Core/Protocol 순수 로직 테스트는 별도 설정 없이 바로 실행된다.
`SafetyVision.Tests/Data/` 아래 DB 통합 테스트(저장 중복 방지, 통계 집계)는 **실제 MySQL이 필요**하며, 데모 DB(`safetyvision`)를 더럽히지 않도록 별도 테스트 DB를 쓴다:

```powershell
# 최초 1회
mysql -u root -e "CREATE DATABASE IF NOT EXISTS safetyvision_test CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci; GRANT ALL PRIVILEGES ON safetyvision_test.* TO 'safetyvision_app'@'localhost';"
$env:SAFETYVISION_MYSQL_CONNSTR = "Server=localhost;Port=3306;Database=safetyvision_test;User=safetyvision_app;Password=<비밀번호>;SslMode=None;AllowPublicKeyRetrieval=True;"
dotnet ef database update --project src/SafetyVision.Data --startup-project src/SafetyVision.Data

# 테스트 실행 시
$env:SAFETYVISION_TEST_MYSQL_CONNSTR = "Server=localhost;Port=3306;Database=safetyvision_test;User=safetyvision_app;Password=<비밀번호>;SslMode=None;AllowPublicKeyRetrieval=True;"
dotnet test tests/SafetyVision.Tests
```

환경변수가 없으면 DB 통합 테스트만 명확한 안내 메시지와 함께 실패하고(순수 로직 테스트는 영향 없음), 해당 클래스들은 `[Collection("MySqlIntegration", DisableParallelization = true)]`로 묶어 같은 테이블을 공유하는 테스트끼리 병렬 실행으로 간섭하지 않게 했다.

## 8. Day1~6 검증 결과 요약

- `dotnet build` (Client/Server/Core/Protocol/Data) 전체 0 오류.
- xUnit 48개 테스트 통과: 판정 로직 7개 예제, 상태 머신 시나리오, ROI/PPE 연결(모호한 PPE 제외 포함), TCP 프레이밍(정상/분할 전송/경계값 초과/음수 길이/빈 페이로드), PBKDF2, 설정 검증(비밀번호·리슨 주소), **DB 통합(동일 InspectionKey 재시도 중복 방지, 항목 정확히 3개, 통계 분모에 NOT_WORN/UNKNOWN 포함, 0건 처리, 최근 10건 정렬)**.
- 실제 ONNX 모델 로드 및 metadata 검증 성공. **실제 정지 이미지(목업의 작업자 사진)로 추론까지 실행해 Safety Vest(conf 0.61~0.67), Hardhat(conf 0.44~0.68)를 실제로 검출함을 확인**(letterbox 전처리 → 세션 실행 → NMS 후처리 → 좌표 역변환 전체 경로가 실동작). 이 사진에서는 인물이 상반신 위주로 잘려 있어 Person 클래스는 임계값 이상으로 잡히지 않았는데, 이는 사진 구도 문제이지 클래스 매핑 오류가 아니다(다른 클래스는 정상 매핑·검출됨). 실제 웹캠 전신 샷에서 재확인 필요.
- 실제 MySQL 연결, 마이그레이션 적용, admin Seed 확인.
- 실제 TCP 클라이언트로 로그인 실패/성공, 대시보드 조회 End-to-End 확인.
- WPF 클라이언트(로그인/대시보드/현장검사/결과/이력) 구현 완료, 전체 솔루션 빌드 확인. 연결 끊김 시 모든 화면은 현재 세션을 폐기하고 로그인 화면으로 복귀 안내.

## 9. Day6 예외·경계 시나리오 실행 검증

실제 서버 프로세스를 띄우고 raw TCP 클라이언트로 아래 시나리오를 직접 재현해 확인했다(스크립트는 재현 목적의 임시 코드이며 저장소에는 포함하지 않음):

| 시나리오 | 결과 |
|---|---|
| 클라이언트 5개 동시 접속 후 동시 로그인 | 5/5 성공, 세션 간 간섭 없음 |
| TCP 비정상 종료(RST)로 세션 끊긴 뒤 신규 접속 | 서버 프로세스 생존, 새 연결 정상 로그인 성공 (세션 정리 확인) |
| 같은 연결에서 로그인 실패 2회 후 성공 | 연결이 끊기지 않고 유지됨(치명적 오류가 아닌 요청 오류는 연결 유지) |
| 모델 파일 제거 후 기동 → FrameMeta 전송 | 서버는 정상 기동(DB/TCP는 살아있음)하고, 검사 요청에는 `ErrorNotification(MODEL_UNAVAILABLE)` 응답, 크래시 없음 |
| MySQL 중지 후 서버 기동 → 클라이언트 접속 | TCP 연결 자체는 수립되나 서버가 즉시 종료(신규 클라이언트 거부), 클라이언트는 쓰기 시점에 연결 끊김 예외로 확인 |

## 10. 알려진 한계 (아직 실행 검증 필요)

- 실제 웹캠을 통한 검출 품질, 발표 PC 추론 속도, 클라이언트→서버 프레임 전송 지연은 실물 웹캠 환경에서 확인 필요(이 개발 환경은 대화형 데스크톱 세션이 없어 WPF 창을 띄워 육안 확인 불가).
- 다중 클라이언트 동시 접속은 TCP 세션 격리 수준까지 확인했으나(§10), 실제 웹캠 2대 이상 동시 시연은 미실시.
- MySQL Windows 서비스 등록은 발표 PC에서 관리자 권한으로 별도 진행 필요.
- 저장 실패(파일시스템 오류 등) 경로는 코드 레벨 리뷰와 재시도 중복 방지 테스트로만 확인했고, 실제 장애 주입 시연은 하지 않음.
