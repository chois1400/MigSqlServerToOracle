# SQL Server to Oracle Migration Tool

이 프로그램은 SQL Server 데이터베이스에서 Oracle 데이터베이스로 데이터를 이전하는 .NET Core 콘솔 애플리케이션입니다.

## 기능

- ✅ SQL Server에서 배치 단위로 데이터 읽기
- ✅ 데이터 타입 자동 매핑 (SQL Server → Oracle)
- ✅ 트랜잭션 기반 데이터 삽입 (배치별)
- ✅ 포괄적인 오류 처리 및 로깅
- ✅ 설정 기반 배치 크기 및 타임아웃 조정
- ✅ 테이블 자동 검색 기능

## 필수 요구사항

- .NET 8.0 이상
- SQL Server 2016 이상
- Oracle Database 11g 이상
- Visual Studio Code 또는 Visual Studio

## 설치 및 설정

### 1. 연결 문자열 설정

`appsettings.json` 파일을 편집하여 SQL Server 및 Oracle 연결 문자열을 설정하세요:

```json
{
  "ConnectionStrings": {
    "SqlServer": "Server=YOUR_SQL_SERVER;Database=YOUR_DATABASE;User Id=sa;Password=YOUR_PASSWORD;Encrypt=true;TrustServerCertificate=true;",
    "Oracle": "Data Source=YOUR_ORACLE_TNS;User Id=YOUR_USER;Password=YOUR_PASSWORD;"
  }
}
```

### 2. 마이그레이션 코드 작성

`Program.cs`에서 다음 섹션을 수정하여 마이그레이션할 테이블을 지정하세요:

```csharp
// 특정 테이블 마이그레이션
string sourceTable = "dbo.YourTableName";
string targetTable = "YOUR_TABLE_NAME";  // Oracle 테이블명 (일반적으로 대문자)

logger.LogInformation($"Starting migration of table '{sourceTable}'");
await migrationService.MigrateTableAsync(sourceTable, targetTable);
logger.LogInformation("Migration completed successfully");
```

## 사용 방법

### 프로젝트 빌드

```bash
dotnet build
```

### 프로그램 실행

```bash
dotnet run
```

### 디버그 모드 실행

```bash
dotnet run --configuration Debug
```

## 주요 클래스

### MigrationService

- **GetSourceTablesAsync()**: SQL Server의 모든 테이블 목록 조회
- **MigrateTableAsync(sourceTable, targetTable)**: 특정 테이블 마이그레이션 (배치 처리)
- **TruncateOracleTableAsync(tableName)**: Oracle 테이블 데이터 삭제

## 배치 처리 방식

- 기본 배치 크기: 1,000행 (appsettings.json에서 조정 가능)
- 각 배치는 별도의 트랜잭션으로 처리
- 배치 실패 시 해당 배치 트랜잭션만 롤백

## 성능 최적화 팁

1. **배치 크기 조정**: appsettings.json의 `BatchSize`를 증가시키면 성능 향상 (메모리 사용량 증가)
2. **타임아웃 설정**: 대량 데이터 이전 시 `CommandTimeout`을 증가시키세요
3. **인덱스**: Oracle 대상 테이블에서 불필요한 인덱스를 임시 비활성화하면 삽입 속도 향상

## 데이터 타입 매핑

| SQL Server | Oracle |
|-----------|--------|
| bigint | NUMBER(19,0) |
| int | NUMBER(10,0) |
| smallint | NUMBER(5,0) |
| decimal(p,s) | NUMBER(p,s) |
| varchar(n) | VARCHAR2(n) |
| nvarchar(n) | NVARCHAR2(n) |
| char(n) | CHAR(n) |
| nchar(n) | NCHAR(n) |
| datetime/datetime2 | TIMESTAMP |
| date | DATE |
| bit | NUMBER(1,0) |
| float | FLOAT(126) |
| real | REAL |

> 참고: 현재 구현에서는 Dapper를 통해 자동 매핑을 수행합니다. 복잡한 매핑이 필요한 경우 `MigrationService.cs`의 `InsertIntoOracleAsync` 메서드를 확장하세요.

## 재시작 및 복구

마이그레이션을 다시 실행하려면 다음과 같이 Oracle 테이블을 초기화합니다:

```csharp
await migrationService.TruncateOracleTableAsync("YOUR_TABLE_NAME");
```

## 로깅

프로그램은 다음 정보를 콘솔에 출력합니다:

- 마이그레이션 시작/종료
- 배치별 진행 상황
- 오류 및 예외 메시지
- 총 마이그레이션 행 수

## 문제 해결

### 연결 오류
- SQL Server 및 Oracle 연결 문자열을 확인하세요
- 방화벽 설정을 확인하세요
- 사용자 권한을 확인하세요

### 타임아웃 오류
- `appsettings.json`의 `CommandTimeout`을 증가시키세요
- 배치 크기를 줄여보세요

### 데이터 타입 불일치
- Oracle 테이블 스키마와 SQL Server 소스 테이블의 컬럼 타입을 확인하세요

## 라이센스

MIT License

## 기여

개선 사항이나 버그 리포트는 이슈로 등록해주세요.
