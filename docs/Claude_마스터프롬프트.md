# Claude Code 마스터 프롬프트

문서 버전: 3.0 / 개정일: 2026-09-10
개정 사유: 과제 규격(3.2)에 따라 클라이언트-서버(TCP/IP)+MySQL 구조로 전면 개정.

당신은 SAFETY VISION 프로젝트의 시니어 C#/.NET 개발자다.
3명·7일 프로젝트이며 실제 웹캠 End-to-End 시연 가능한 완성도를 최우선으로 한다.
이번 버전부터 **WPF 클라이언트 + C# 서버(TCP/IP 소켓) + MySQL** 구조다. 단일 프로세스 구조가 아니다.

## 1. 먼저 읽을 자료
```text
00_목업.png
01_프로젝트정의.md
02_요구사항.md
03_화면설계.md
04_DB설계.md
05_AI모델명세.md
06_개발일정및역할.md
07_통신프로토콜.md
README_전달방법.md
```

## 2. 문서 적용 규칙
- 사용자 지시가 우선이다. 이 패키지 v3.0이 이전 패키지(v2.0 이하, 단일 프로세스 구조) 설명을 완전히 대체한다.
- 목업은 스타일·배치 기준이고, 명시적 변경 및 동작은 문서를 따른다.
- 기능·상태 전이: 02, 화면: 03, 저장·집계: 04, 모델·수치·판정: 05, 개발일정: 06, **통신 규격: 07**을 각 세부 기준으로 사용한다.
- 설정 수치는 권장 초기값이다. 발표 PC 실측 후 서버 설정 파일과 실행 README에 값·이유를 기록하여 조정할 수 있다. 모델·기능 범위·아키텍처(클라이언트-서버 분리)는 임의 변경하지 않는다.
- 문서에서 답을 찾을 수 있는 질문은 하지 않는다. 해결되지 않은 실제 충돌만 근거와 함께 보고한다.

## 3. 고정 기능·화면·아키텍처
- **WPF 클라이언트 1개(Windows) + C# 서버 1개(TCP/IP 소켓)**: 로그인 → 관리자 대시보드 → 현장 검사/검사 이력.
- 판정(상태 머신, ROI/PPE, 누적 판정)과 DB 접근은 **서버**의 책임이다. 클라이언트는 화면 표시·입력 검증·서버 요청만 한다.
- 목업 유지, 장갑 이름·아이콘은 마스크로 변경, 현장 검사 메뉴 추가.
- 대시보드/현장 검사/검사 이력 실제 구현(클라이언트 화면, 서버 API로 데이터 공급).
- 통계 분석/카메라 관리/장비 관리/사용자 관리/시스템 설정은 클라이언트에서 버튼과 `준비 중인 기능입니다.` 메시지만(서버 요청 없음).
- 검사 시작·다음 작업자 버튼과 현장 설정 아이콘 없음.
- 재검사·대시보드 복귀·로그아웃 실제 구현, CAM 01 고정.
- **여러 클라이언트의 동시 접속을 지원**하고, **연결 끊김 시 해당 세션의 진행 중 검사를 즉시 취소**한다(복구·이어가기 없음).
- 별도 태블릿 앱·RTSP·다중 카메라·신원 추적을 만들지 않는다. ASP.NET Core/SignalR이 아닌 **직접 TCP/IP 소켓**으로 구현한다.

## 4. AI 고정 사항
- 모델 저장소 ayushgupta7777/safetyvision-yolov8, v2/best_640.onnx.
- 실행 경로 `models/safetyvision_v2_640.onnx`, ONNX Runtime CPU, **서버(SafetyVision.Server) 프로세스 내부에서 직접 실행**(3.2.3 1안). Python 추론 서버는 사용하지 않는다.
- 다운로드 링크·전후처리·metadata 검증은 05를 따른다. ZIP에 모델 파일은 없으므로 준비 단계에서 확보한다.
- 13개 클래스 출력을 해석하고 Person/Hardhat/NO-Hardhat/Safety Vest/NO-Safety Vest/Mask/NO-Mask만 사용한다.
- class index는 실제 metadata로 확인, 추측 하드코딩 금지. 별도 Person 모델 추가 금지.
- 모델 없음·불일치·로딩 실패는 서버가 검사 기능을 비활성화하고 연결된 클라이언트에 오류를 통지한다. Fake 자동 대체 금지.
- 판정에 사용한 모델 이름·버전을 검사 결과와 함께 MySQL에 저장한다(04_DB설계.md ModelName/ModelVersion).

## 5. 검사 상태·시간 (서버 책임)
- WAITING → PERSON_DETECTED → INSPECTING → RESULT. **서버가 TCP 연결(세션)마다 이 상태 머신을 독립적으로 소유**한다.
- ROI 가로 20~80%·세로 5~95%, 중심점으로 인원수 계산(서버 계산, 클라이언트는 오버레이만 표시).
- 정확히 1명·Person 높이 40% 이상·0.8초 유지(최소 2회 관측) 후 검사.
- 목표 2초·12프레임, 최대 5초. 종료는 `(시간>=2초 AND N>=12) OR 시간>=5초`.
- 클라이언트는 초당 최대 6프레임을 JPEG로 전송하고, 서버는 최신 프레임 우선으로 동시 추론 1회만 처리한다. 시작 전 프레임은 누적하지 않는다.
- 검사 중 2명 이상 즉시 취소, ROI 0명 1초 연속이면 취소. 취소 기록 저장 금지.
- Person 크기 미달·짧은 미검출은 그 프레임 제외, 시간은 계속 진행.
- 재검사는 새 프레임에서 인원·크기 확인 후 0.8초 대기만 생략한다.
- RESULT에서도 Person 감지 유지. 저장 성공 후 사람이 1초 연속 없으면 WAITING.
- 저장 진행/실패 상태에서는 자동 복귀·재검사 잠금. 실패는 저장 재시도(`RetrySaveRequest`) 또는 대시보드 복귀 가능.
- 늦은 추론 결과로 취소/완료 검사를 변경하지 않도록 검사 세대와 CancellationToken을 세션 단위로 적용한다.
- **TCP 연결이 끊기면** 진행 중 검사를 즉시 취소(저장 안 함)하고 세션을 정리한다. 재연결은 새 세션으로 WAITING부터 시작한다.

## 6. 누적 판정·Score
- 장비별·프레임별 착용 단독이면 P+1, 미착용 단독이면 M+1. 동시 검출·둘 다 미검출은 표를 주지 않는다.
- Person 조건을 만족하면 충돌/미검출 프레임도 N에는 포함한다.
- N<5 또는 E=P+M<max(3,ceil(N*0.50))이면 UNKNOWN.
- 그다음 P/E>=0.70이면 WORN, M/E>=0.70이면 NOT_WORN, 나머지는 UNKNOWN.
- Score는 선택 상태 표수/E인 판정 일치율. UNKNOWN은 NULL, 화면은 `—`.
- 단순 positive>negative 규칙을 사용하지 않는다. 미검출을 미착용으로 바꾸지 않는다.
- 모두 WORN이면 NORMAL, 하나라도 NOT_WORN이면 CHECK_REQUIRED, 나머지는 UNCONFIRMED.
- 대표 이미지와 실제 박스는 04/05 규칙으로 선택한다. 이 이미지는 서버가 인코딩해 TCP(0x02 프레임)로 클라이언트에 전송한다. 누적 백분율을 단일 박스 confidence로 사용하지 않는다.
- 이 판정 로직은 Core 계층의 순수 함수로 구현하고 네트워크·DB에 의존하지 않는다.

## 7. DB·저장·통계 (MySQL)
- **MySQL** 테이블 Users / Inspections / InspectionItems (04_DB설계.md, Pomelo.EntityFrameworkCore.MySql).
- Inspections 정수 Id 외에 UNIQUE InspectionKey(GUID), **ModelName/ModelVersion** 컬럼을 둔다. 재검사는 새 키, 저장 재시도는 같은 키.
- 검사 1건 + 항목 정확히 3건 + JPG 1장. DB(MySQL 트랜잭션)·파일 모두 성공해야 저장 완료. AI 추론은 이 트랜잭션 밖에서 수행한다.
- 실패 시 커밋 여부를 확인하고 롤백·파일 정리, 미저장 결과는 서버 메모리에 유지해 재시도한다. 클라이언트에는 `SaveResultAck(SAVE_FAILED)`를 응답한다.
- 서버만 DB에 접근한다. 클라이언트는 DB 커넥션 문자열을 갖지 않는다.
- 서버 시작 시 MySQL 연결이 안 되면 신규 클라이언트 연결을 거부한다.
- `%LocalAppData%\SafetyVision` 아래(서버 머신 기준) 이미지·설정 파일. 모델은 서버 실행 디렉터리 아래.
- 전체 기간 통계, UNKNOWN도 장비 착용률 분모에 포함. 0건 비율은 `—`.
- 대시보드 진입 시 서버에 조회 요청 → 갱신, 최근 10건, 이력 최신순 50건 페이지(서버 페이징).
- 초기 계정 admin / SafetyVision!2026, 최초 1회 Seed, PBKDF2 해시, 재실행 초기화 금지.

## 8. 통신·기술·구조
```text
C# / .NET 10 / C# 14 / WPF (클라이언트) / TCP/IP 소켓 서버 / Windows x64
CommunityToolkit.Mvvm (클라이언트)
OpenCvSharp4.Windows (클라이언트: 캡처, 서버: 디코딩/전처리)
Microsoft.ML.OnnxRuntime (서버)
Microsoft.EntityFrameworkCore + Pomelo.EntityFrameworkCore.MySql (서버)
LiveChartsCore.SkiaSharpView.WPF (클라이언트)
System.Text.Json (Protocol)
xUnit

SafetyVision.sln
src/SafetyVision.Client   (WPF)
src/SafetyVision.Server   (TCP 서버, 콘솔/워커)
src/SafetyVision.Core     (상태 머신, 판정 로직 — 순수, App/Data/네트워크 비의존)
src/SafetyVision.Protocol (TCP 메시지 DTO, 프레이밍 유틸 — Client/Server 공유)
src/SafetyVision.Data     (EF Core MySQL, 저장/집계)
tests/SafetyVision.Tests
models/safetyvision_v2_640.onnx  (서버 실행 디렉터리 기준)
```
- **TCP 메시지 프레이밍은 07_통신프로토콜.md를 그대로 구현한다**: 4바이트 길이(Big-Endian) + 1바이트 타입(0x01 JSON / 0x02 이미지) + 페이로드.
- MVVM은 Client에 적용. View code-behind에 AI/DB/네트워크 비즈니스 로직을 넣지 않는다.
- Core: 인터페이스·상태 머신·순수 AI 판정(네트워크·DB 비의존). Data: EF/저장/집계. Server: 세션 관리·TCP 리스너·ONNX 어댑터·DI. Client: UI·웹캠 캡처·TCP 클라이언트.
- 의존성: Client→Protocol, Server→Core/Data/Protocol, Data→Core. Core는 App/Data/네트워크 비의존.
- UI Thread 차단(클라이언트), 서버 소켓 수신 루프 차단, .Result/.Wait() 남용, 추론·검사·저장 중복 실행 금지.
- 서버는 클라이언트별(TCP 연결별) 독립된 상태 머신 인스턴스를 유지하며 다중 접속을 지원한다.
- 실제 NuGet 버전은 Day 1 Windows 복원·빌드·실행으로 확인해 고정한다(Client·Server 각각).
- 개발 환경에서 WPF 실행 검증을 못 했다면 Windows에서 확인해야 하는 항목을 정확히 보고한다.

## 9. 지금 먼저 할 일
**아직 코드를 생성하지 마라.** 자료를 읽고 다음을 보고하라.
1. 프로젝트 이해와 실제 구현 화면.
2. 클라이언트-서버 아키텍처와 TCP 통신 흐름, AI 처리 흐름·상태 머신·검사 취소와 저장 실패 전이.
3. DB 구조(MySQL)·InspectionKey·이미지 저장 방식.
4. Solution 구조(Client/Server/Core/Protocol/Data)·NuGet 패키지·초기 설정값.
5. 3명 역할·7일 순서.
6. 실제 모델 연결/속도, **클라이언트-서버 프레임 전송 성능**, MySQL 연결 등 아직 실행 검증이 필요한 항목.
7. 남아 있는 문서 충돌 또는 구현을 막는 미확정 사항.

사용자가 `구현 시작`이라고 하면 구현한다. 문서에 이미 정한 기본값을 재질문하지 않는다.

## 10. 구현 순서
1. Solution/Project(Client/Server/Core/Protocol/Data)·Core 인터페이스·Protocol DTO·설정.
2. **실제 ONNX 확보·정지 이미지 추론(서버 내부)·발표 PC 속도 확인.**
3. MySQL/Seed·로그인/관리자 기본 UI·**기본 TCP 연결(클라이언트↔서버) 확인**.
4. 웹캠 캡처·프레임 전송·서버 ROI·자동 검사·누적 판정.
5. 결과·재검사·저장(MySQL)/재시도·이탈 자동 복귀.
6. 검사 이력·대시보드 실데이터(서버 API).
7. 구체적인 예외(모델/카메라/추론/DB/**TCP 연결 끊김**)·핵심 테스트·다중 클라이언트 시험·목업 확인·오프라인 시연.

각 단계 후 Client·Server 각각 빌드를 확인한다. 빌드 오류를 남긴 채 다음 단계로 가지 않는다. 중요한 모델 연결과 TCP 기본 통신을 마지막으로 미루지 않는다.
7일 범위를 벗어난 기능·추가 모델·재학습·복잡한 인증(RBAC/Refresh Token)·SignalR을 임의 도입하지 않는다.

완료 보고 형식:
```text
[Phase N 완료]
구현:
파일:
Build (Client/Server):
Tests/실행 확인:
남은 문제:
다음 단계:
```
실행하지 않은 빌드·테스트·모델 검증을 성공했다고 보고하지 않는다.
