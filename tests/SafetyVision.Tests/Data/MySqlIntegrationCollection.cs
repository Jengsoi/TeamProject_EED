using Xunit;

namespace SafetyVision.Tests.Data;

// DB 통합 테스트들은 같은 테스트 DB의 Inspections/InspectionItems 테이블을 공유하므로
// 클래스 간 병렬 실행을 막아 서로의 초기화(Reset)와 간섭하지 않게 한다.
[CollectionDefinition("MySqlIntegration", DisableParallelization = true)]
public sealed class MySqlIntegrationCollection;
