# SafetyVision — 실행 README

산업안전 AI 보호구 착용 검사 시스템. WPF 클라이언트 + C# TCP 서버 + MySQL 구조 (패키지 v3.0 기준).
문서 원본: `docs/` 폴더 (00~07, README_전달방법.md, Claude_마스터프롬프트.md).

## 1. 사전 준비

- .NET 10 SDK (확인된 버전: 10.0.401)
- MySQL 8.x (개발 확인: MySQL Server 8.4.9 — winget `Oracle.MySQL` 카탈로그에 정확한 8.0.x가 없어 8.4 LTS로 진행. SQL 문법은 8.0과 사실상 호환되며, 서버는 `ServerVersion.AutoDetect`로 실제 버전을 인식한다.)
- Windows x64 (WPF 클라이언트 실행 환경)

## 2. 모델 준비

- PPE 모델: `src/SafetyVision.Server/models/safetyvision_v2_896.onnx` (입력 896, 안전모·안전조끼·마스크 후보 검출).
- 사람 검출 모델: `src/SafetyVision.Server/models/yolov8n.onnx` (입력 640, COCO `names` 메타데이터가 포함된 로컬 파일).
- 현재 마스크 판정은 PPE 모델의 Mask/NO-Mask 후보를 사용한다. 별도 얼굴·마스크 분류 모델은 실험용이며 서버에서 로드하지 않는다.
- 서버 모델의 `.onnx` 파일은 `.gitignore` 대상이다. 새 환경에서는 `src/SafetyVision.Server/models/README.md`의 출처를 확인해 두 모델을 같은 경로에 배치한다. 별도 실험 모델은 `experiments/mask-models/`에 있다.

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
Windows의 Debug 실행에서는 `AutoStartLocalMySql`이 켜져 있으면 서버가 포트 3306을 확인하고, MySQL이 꺼져 있을 때 `LocalMySqlExecutable`과 `LocalMySqlConfigFile` 경로로 시작을 시도한다. 설치 경로가 다르면 `src/SafetyVision.Server/appsettings.json`에서 변경한다.

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

Visual Studio에서는 `SafetyVision.slnx`를 열고 시작 프로필 **SafetyVision (서버 + 클라이언트)**를 선택해 F5를 누른다. `SafetyVision.slnLaunch`가 서버와 클라이언트를 함께 시작하도록 설정하며, `SafetyVision.Core` 같은 클래스 라이브러리를 시작 프로젝트로 선택하면 실행 오류가 난다.

터미널에서는 각각 실행한다:

```powershell
# 서버
dotnet run --project src/SafetyVision.Server

# 클라이언트 (별도 터미널/PC)
dotnet run --project src/SafetyVision.Client
```

서버 기본 리슨 포트: `8910` (`appsettings.json`의 `ListenPort`).

## 6. 설정값과 근거

서버 설정 파일: `src/SafetyVision.Server/appsettings.json`. 아래 값은 현재 로컬 설정이며, 옛 문서의 초기 권장값과 다를 수 있다. 추론 속도·프레임 전송 지연은 발표 PC에서 다시 확인한다.

| 항목 | 값 | 상태 |
|---|---|---|
| ModelInputSize / PersonModelInputSize | 896 / 640 | 현재 ONNX 입력 크기 |
| DetectionConfidence / MaskDetectionConfidence | 0.40 / 0.00001 | PPE 기본값 / 낮은 마스크 후보 보존 |
| NmsIouThreshold | 0.45 | 현재 설정 |
| TargetAnalysisFrames / MinAnalysisFrames | 8 / 5 | 현재 설정 |
| MaxInferenceFps | 4 | 현재 설정 |
| DecisionRatio | 0.70 | 현재 설정 |

대시보드의 장비별 현황은 착용률 막대 위의 퍼센트가 잘리지 않도록 위쪽 여백을 두고 표시한다. 최근 검사는 최신 5건만 보여 주며 전체 내역은 검사 이력 화면에서 확인한다.

## 7. 테스트 실행

```powershell
dotnet test tests/SafetyVision.Tests
```

Core/Protocol 순수 로직 테스트는 별도 설정 없이 바로 실행된다.
`SafetyVision.Tests/Data/` 아래 DB 통합 테스트(저장 중복 방지, 통계 집계)는 **실제 MySQL이 필요**하며, 데모 DB(`safetyvision`)를 더럽히지 않도록 별도 테스트 DB를 쓴다:

```powershell
# 최초 1회
mysql -u root -e "CREATE DATABASE IF NOT EXISTS safetyvision_test CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci; GRANT ALL PRIVILEGES ON safetyvision_test.* TO 'safetyvision_app'@'localhost';"
$env:SAFETYVISION_MYSQL_CONNSTR = "Server=localhost;Port=3306;Database=safetyvision_test;User=safetyvision_app;Password=<비밀번호>;"
dotnet ef database update --project src/SafetyVision.Data --startup-project src/SafetyVision.Data

# 테스트 실행 시
$env:SAFETYVISION_TEST_MYSQL_CONNSTR = "Server=localhost;Port=3306;Database=safetyvision_test;User=safetyvision_app;Password=<비밀번호>;"
dotnet test tests/SafetyVision.Tests
```

환경변수가 없으면 DB 통합 테스트만 명확한 안내 메시지와 함께 실패하고(순수 로직 테스트는 영향 없음), 해당 클래스들은 `[Collection("MySqlIntegration", DisableParallelization = true)]`로 묶어 같은 테이블을 공유하는 테스트끼리 병렬 실행으로 간섭하지 않게 했다.

## 8. Day1~6 검증 결과 요약

- `dotnet build` (Client/Server/Core/Protocol/Data) 전체 0 오류.
- xUnit 44개 테스트 통과: 판정 로직 7개 예제, 상태 머신 시나리오, ROI/PPE 연결(모호한 PPE 제외 포함), TCP 프레이밍(정상/분할 전송/경계값 초과/음수 길이/빈 페이로드), PBKDF2, **DB 통합(동일 InspectionKey 재시도 중복 방지, 항목 정확히 3개, 통계 분모에 NOT_WORN/UNKNOWN 포함, 0건 처리, 최근 10건 정렬)**.
- 실제 ONNX 모델 로드 및 metadata 검증 성공. **실제 정지 이미지(목업의 작업자 사진)로 추론까지 실행해 Safety Vest(conf 0.61~0.67), Hardhat(conf 0.44~0.68)를 실제로 검출함을 확인**(letterbox 전처리 → 세션 실행 → NMS 후처리 → 좌표 역변환 전체 경로가 실동작). 이 사진에서는 인물이 상반신 위주로 잘려 있어 Person 클래스는 임계값 이상으로 잡히지 않았는데, 이는 사진 구도 문제이지 클래스 매핑 오류가 아니다(다른 클래스는 정상 매핑·검출됨). 실제 웹캠 전신 샷에서 재확인 필요.
- 실제 MySQL 연결, 마이그레이션 적용, admin Seed 확인.
- 실제 TCP 클라이언트로 로그인 실패/성공, 대시보드 조회 End-to-End 확인.
- WPF 클라이언트(로그인/대시보드/현장검사/결과/이력) 구현 완료, 전체 솔루션 빌드 확인. 연결 끊김 시 대시보드/이력 화면은 로그인 화면으로 복귀 안내, 현장 검사 화면은 자체 재연결(최대 5회) 후 실패 시 안내.

## 10. Day6 예외·경계 시나리오 실행 검증

실제 서버 프로세스를 띄우고 raw TCP 클라이언트로 아래 시나리오를 직접 재현해 확인했다(스크립트는 재현 목적의 임시 코드이며 저장소에는 포함하지 않음):

| 시나리오 | 결과 |
|---|---|
| 클라이언트 5개 동시 접속 후 동시 로그인 | 5/5 성공, 세션 간 간섭 없음 |
| TCP 비정상 종료(RST)로 세션 끊긴 뒤 신규 접속 | 서버 프로세스 생존, 새 연결 정상 로그인 성공 (세션 정리 확인) |
| 같은 연결에서 로그인 실패 2회 후 성공 | 연결이 끊기지 않고 유지됨(치명적 오류가 아닌 요청 오류는 연결 유지) |
| 모델 파일 제거 후 기동 → FrameMeta 전송 | 서버는 정상 기동(DB/TCP는 살아있음)하고, 검사 요청에는 `ErrorNotification(MODEL_UNAVAILABLE)` 응답, 크래시 없음 |
| MySQL 중지 후 서버 기동 → 클라이언트 접속 | TCP 연결 자체는 수립되나 서버가 즉시 종료(신규 클라이언트 거부), 클라이언트는 쓰기 시점에 연결 끊김 예외로 확인 |

## 11. 알려진 한계 (아직 실행 검증 필요)

- 실제 카메라 화면에서 마스크 오인식 사례가 관찰됐다. 안전모·마스크·조끼의 착용/미착용 조합, 발표 PC 추론 속도, 클라이언트→서버 프레임 전송 지연은 추가 현장 검증이 필요하다.
- 다중 클라이언트 동시 접속은 TCP 세션 격리 수준까지 확인했으나(§10), 실제 웹캠 2대 이상 동시 시연은 미실시.
- MySQL Windows 서비스 등록은 발표 PC에서 관리자 권한으로 별도 진행 필요.
- 저장 실패(파일시스템 오류 등) 경로는 코드 레벨 리뷰와 재시도 중복 방지 테스트로만 확인했고, 실제 장애 주입 시연은 하지 않음.
