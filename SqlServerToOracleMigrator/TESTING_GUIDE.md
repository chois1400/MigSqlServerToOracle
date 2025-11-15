# 마이그레이션 테스트 및 실행 가이드

## 테스트 환경 구성

### 1단계: SQL Server 테스트 데이터 생성

**로컬 SQL Server 또는 SQL Server Express에서:**

```bash
sqlcmd -S (localdb)\mssqllocaldb -i CreateTestData.sql
```

또는 SQL Server Management Studio (SSMS)에서 `CreateTestData.sql` 파일을 열고 실행하세요.

### 2단계: Oracle 테스트 테이블 생성

**Oracle SQL*Plus에서:**

```bash
sqlplus username/password@instance @CreateOracleTestTables.sql
```

또는 Oracle SQL Developer에서 `CreateOracleTestTables.sql` 파일을 열고 실행하세요.

### 3단계: 연결 문자열 설정

`appsettings.json` 파일을 환경에 맞게 수정하세요:

```json
{
  "ConnectionStrings": {
    "SqlServer": "Server=(localdb)\\mssqllocaldb;Database=MigrationTestDB;Integrated Security=true;Encrypt=true;TrustServerCertificate=true;",
    "Oracle": "Data Source=YOUR_ORACLE_TNS;User Id=YOUR_USER;Password=YOUR_PASSWORD;"
  },
  "MigrationSettings": {
    "BatchSize": 1000,
    "CommandTimeout": 300
  }
}
```

**SQL Server 연결 문자열 예제:**
- 로컬 기본: `Server=(local);Database=MigrationTestDB;Integrated Security=true;`
- SQL Server Express: `Server=(localdb)\mssqllocaldb;Database=MigrationTestDB;Integrated Security=true;`
- 원격 서버: `Server=192.168.1.100;Database=MigrationTestDB;User Id=sa;Password=YourPassword;`

**Oracle 연결 문자열 예제:**
- 로컬: `Data Source=localhost:1521/ORCL;User Id=system;Password=oracle;`
- 원격: `Data Source=oracle_host:1521/ORCL;User Id=username;Password=password;`

## 프로그램 실행

### 빌드

```bash
dotnet build
```

### 실행 (모든 테이블 목록 조회)

```bash
dotnet run
```

### 마이그레이션 실행

`Program.cs`에서 다음 주석 처리된 부분을 수정하여 활성화하세요:

```csharp
// 특정 테이블 마이그레이션
string sourceTable = "dbo.Employees";
string targetTable = "EMPLOYEES";

logger.LogInformation($"Starting migration of table '{sourceTable}'");
await migrationService.MigrateTableAsync(sourceTable, targetTable);
logger.LogInformation("Migration completed successfully");
```

### 릴리스 빌드 및 실행

```bash
dotnet publish -c Release -o ./bin/Release/publish
./bin/Release/publish/SqlServerToOracleMigrator.exe
```

## 테스트 시나리오

### 시나리오 1: 모든 테이블 목록 조회
- SQL Server 연결만 필요
- `GetSourceTablesAsync()` 메서드 호출

### 시나리오 2: 단일 테이블 마이그레이션
- SQL Server & Oracle 모두 연결 필요
- 배치 크기: 1,000행
- 트랜잭션 기반 처리

### 시나리오 3: 대량 데이터 마이그레이션
- 배치 크기를 5,000 이상으로 증가
- `MigrationSettings:BatchSize` 수정

### 시나리오 4: 기존 데이터 재마이그레이션
- `TruncateOracleTableAsync()` 호출하여 대상 테이블 초기화
- 다시 마이그레이션 시작

## 마이그레이션 완료 후 검증

### SQL Server에서 행 수 확인

```sql
SELECT TABLE_NAME, 
       (SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES t WHERE t.TABLE_NAME = INFORMATION_SCHEMA.TABLES.TABLE_NAME) AS ROW_COUNT
FROM INFORMATION_SCHEMA.TABLES
WHERE TABLE_TYPE = 'BASE TABLE'
ORDER BY TABLE_NAME
```

### Oracle에서 행 수 확인

```sql
SELECT table_name, num_rows 
FROM user_tables 
WHERE table_name IN ('EMPLOYEES', 'DEPARTMENTS', 'PROJECTS');
```

또는

```sql
SELECT 'EMPLOYEES' as table_name, COUNT(*) as num_rows FROM EMPLOYEES
UNION ALL
SELECT 'DEPARTMENTS', COUNT(*) FROM DEPARTMENTS
UNION ALL
SELECT 'PROJECTS', COUNT(*) FROM PROJECTS;
```

## 성능 튜닝

### 1. 배치 크기 최적화
- 작은 배치 (100-500): 안정성 중심, 느린 속도
- 중간 배치 (1,000-5,000): 권장 설정
- 큰 배치 (10,000+): 고속, 높은 메모리 사용

### 2. 타임아웃 조정
- 기본값: 300초
- 대량 데이터: 600-1200초로 증가
- `appsettings.json` 수정: `"CommandTimeout": 600`

### 3. Oracle 인덱스 비활성화
```sql
-- 마이그레이션 전
ALTER INDEX idx_name UNUSABLE;

-- 마이그레이션 후
ALTER INDEX idx_name REBUILD;
```

## 문제 해결

### "Could not open a connection to SQL Server"
- SQL Server가 실행 중인지 확인
- 연결 문자열의 서버 이름 확인
- 방화벽 포트 (기본: 1433) 확인

### "ORA-12514: TNS:listener could not resolve the connect identifier"
- Oracle TNS 이름 또는 호스트 확인
- Oracle listener 실행 상태 확인
- `tnsnames.ora` 파일 확인

### "Timeout expired"
- `CommandTimeout` 증가
- 배치 크기 감소
- 대역폭 확인

### 데이터 타입 불일치
- Oracle 테이블의 컬럼 타입과 SQL Server 소스 확인
- `MigrationService.cs`의 타입 매핑 로직 검토

## 다음 단계

마이그레이션 성공 후:

1. **데이터 검증**: 양쪽 데이터베이스의 데이터 비교
2. **애플리케이션 테스트**: 마이그레이션된 데이터로 애플리케이션 테스트
3. **백업**: Oracle 데이터베이스 백업
4. **커밋**: 비즈니스에서 최종 승인 후 확정

---

**마이그레이션 도중 문제가 발생하면 로그를 확인하고 위 문제 해결 섹션을 참고하세요.**
