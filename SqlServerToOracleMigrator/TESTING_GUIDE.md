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

### 시나리오 3: Excel 매핑 파일을 사용한 마이그레이션 (현재 주요 기능)
- Excel 파일에 테이블 매핑 정보 저장
- 여러 테이블을 한 번에 마이그레이션
- 활성화/비활성화 제어 가능
- **마이그레이션 통계 자동 기록** (시간, 상태, 성공/중복/전체 건수)

#### 3-1: 샘플 Excel 파일 생성
```csharp
string mappingFilePath = Path.Combine(Directory.GetCurrentDirectory(), "TableMapping.xlsx");
mappingReader.CreateSampleMappingFile(mappingFilePath);
```

#### 3-2: Excel 파일 읽기 및 마이그레이션
```csharp
string mappingFile = "TableMapping.xlsx";
if (File.Exists(mappingFile))
{
    var mappings = mappingReader.ReadMappingsFromExcel(mappingFile);
    await migrationService.MigrateWithMappingAsync(mappings);
}
```

#### 3-3: 마이그레이션 후 Excel 확인
- **M열 (시작 시간)**: 각 테이블 마이그레이션 시작 시각
- **N열 (완료 시간)**: 각 테이블 마이그레이션 완료 시각
- **O열 (상태)**: "완료", "실패", "진행 중"
- **P열 (소요 시간)**: 초 단위 소요 시간
- **Q열 (성공 레코드 수)**: Oracle에 성공적으로 INSERT된 행 수
- **R열 (오류 메시지)**: 실패 시 오류 내용
- **T열 (중복 건너뜀)**: ORA-00001로 Skip된 행 수
- **U열 (전체 처리)**: SQL Server에서 읽은 전체 행 수

### 시나리오 4: 중복 데이터 처리 검증
- Oracle에 중복 키가 존재하는 상황 시뮬레이션
- 중복 발생 시 자동 Skip 확인
- `DuplicateLogs` 폴더에 로그 파일 생성 확인

#### 4-0: Oracle PK 전체 사용 검증 (Excel V열 지정/자동 추출)
- 목적: 중복 판단과 기존행 조회가 "전체 PK 컬럼"을 사용함을 검증
- 단계:
  1. Excel 매핑의 `V`열에 대상 테이블의 PK 전체 컬럼을 입력합니다. 예: `ZONEID,TRANSACTION_SERIAL_NO`
    - 비워둘 경우 프로그램이 Oracle 메타데이터(OWNER+TABLE 기준)에서 자동 추출합니다.
  2. 마이그레이션 실행 후 `DuplicateLogs/{TABLE}_YYYYMMDD_duplicates.log`를 확인합니다.
  3. 각 로그 라인에서 다음을 확인합니다:
    - `ResolvedOraclePkTargets`가 PK 전체 컬럼을 모두 포함
    - `ExistingOracleWhere`와 `ExistingOracleSelect`에 PK 전체 컬럼이 AND로 결합되어 있음
    - `AttemptedInsertSql`와 `AttemptedInsertParams`가 존재하며 SELECT에도 동일 파라미터가 바인딩됨
  4. 실제 중복 시 `ExistingOracleRowsCount`가 1 이상, 중복이 아니면 0임을 확인합니다.

#### 4-1: 테스트 데이터 준비
```sql
-- SQL Server에 중복 가능성 있는 데이터 삽입
INSERT INTO dbo.TEST_TABLE (ID, NAME) VALUES (1, 'Test1');
INSERT INTO dbo.TEST_TABLE (ID, NAME) VALUES (1, 'Test1'); -- 중복

-- Oracle에 Primary Key 설정
ALTER TABLE TEST_TABLE ADD CONSTRAINT PK_TEST PRIMARY KEY (ID);
```

#### 4-2: 마이그레이션 실행 및 확인
```bash
dotnet run
# 콘솔에서 "Skipping duplicate row" 메시지 확인
# DuplicateLogs/TEST_TABLE_20250101_duplicates.log 파일 확인
```

#### 4-3: 중복 로그 분석
```json
{
  "Timestamp": "2025-01-01T10:30:00",
  "Table": "TEST_TABLE",
  "Data": {
    "ID": 1,
    "NAME": "Test1",
    "INSDTTM": "2024-12-10 10:30:00.1234567"
  },
  "Analysis": {
    "INSDTTM_Original": "2024-12-10 10:30:00.1234567",
    "INSDTTM_ToChar_Simulated": "20241210103000123456700"
  }
}
```

#### 4-4: PK 전체 컬럼 조회 예시 (로그 필드 발췌)
```json
{
  "ResolvedOraclePkTargets": ["ZONEID", "TRANSACTION_SERIAL_NO"],
  "ExistingOracleWhere": "\"ZONEID\" = :k0 AND \"TRANSACTION_SERIAL_NO\" = TO_CHAR(:p5,'YYYYMMDDHHMMSSF9')",
  "ExistingOracleSelect": "SELECT * FROM TB_MCS_STK_ZONE_HIST WHERE \"ZONEID\" = :k0 AND \"TRANSACTION_SERIAL_NO\" = TO_CHAR(:p5,'YYYYMMDDHHMMSSF9') FETCH FIRST 5 ROWS ONLY",
  "AttemptedInsertSql": "INSERT INTO TB_MCS_STK_ZONE_HIST (\"ZONEID\",\"TRANSACTION_SERIAL_NO\",...) VALUES (:p0, TO_CHAR(:p5,'YYYYMMDDHHMMSSF9'), ...)",
  "AttemptedInsertParams": {":p0":"A7STKA1Z0001",":p5":"2025-11-03T10:33:12.39"}
}
```

### 시나리오 5: Primary Key 기반 정렬 검증
- S열이 비어있을 때 자동 PK 조회 확인
- S열에 수동 정렬 컬럼 지정 테스트

#### 5-1: 자동 PK 조회 테스트
```markdown
Excel S열: (비워둠)
기대 결과: 콘솔에 "Primary Key 조회 성공: HIST_SEQNO, INSDTTM, ZONEID" 메시지
```

#### 5-2: 수동 정렬 컬럼 지정 테스트
```markdown
Excel S열: "HIST_SEQNO, INSDTTM, ZONEID"
기대 결과: 지정한 컬럼으로 ORDER BY 적용
```

#### 5-3: PK 조회 실패 시뮬레이션
```sql
-- SQL Server에서 PK가 없는 테이블 생성
CREATE TABLE dbo.NO_PK_TABLE (Col1 INT, Col2 VARCHAR(50));
```
```markdown
기대 결과: 콘솔에 "Primary Key 조회 실패" 경고, ORDER BY 1 사용
```

### 시나리오 4: 대량 데이터 마이그레이션
- 배치 크기를 5,000 이상으로 증가
- `MigrationSettings:BatchSize` 수정

### 시나리오 5: 기존 데이터 재마이그레이션
- Excel F열 (TruncateTarget)을 TRUE로 설정
- 또는 `DeleteOracleTableAsync()` 호출하여 대상 테이블 초기화
- 다시 마이그레이션 시작

## 마이그레이션 완료 후 검증

### SQL Server에서 행 수 확인

```sql
-- 특정 테이블 행 수
SELECT COUNT(*) FROM dbo.TEST_TABLE;

-- 여러 테이블 행 수 한 번에
SELECT 'Employees' as TableName, COUNT(*) as RowCount FROM dbo.Employees
UNION ALL
SELECT 'Departments', COUNT(*) FROM dbo.Departments
UNION ALL
SELECT 'Projects', COUNT(*) FROM dbo.Projects;
```

### Oracle에서 행 수 확인

```sql
-- 테이블 통계 조회 (빠름, 근사치)
SELECT table_name, num_rows 
FROM user_tables 
WHERE table_name IN ('EMPLOYEES', 'DEPARTMENTS', 'PROJECTS');

-- 실제 행 수 카운트 (정확)
SELECT 'EMPLOYEES' as table_name, COUNT(*) as num_rows FROM EMPLOYEES
UNION ALL
SELECT 'DEPARTMENTS', COUNT(*) FROM DEPARTMENTS
UNION ALL
SELECT 'PROJECTS', COUNT(*) FROM PROJECTS;
```

### Excel 파일에서 통계 확인
1. `TableMapping.xlsx` 파일 열기
2. 각 테이블별로 확인:
   - **Q열 (성공 레코드 수)**: Oracle에 INSERT된 행 수
   - **T열 (중복 건너뜀)**: 중복으로 Skip된 행 수
   - **U열 (전체 처리)**: SQL Server에서 읽은 총 행 수
   - **수식 검증**: U = Q + T (전체 = 성공 + 중복)

### 중복 데이터 검증
```bash
# DuplicateLogs 폴더에서 중복 로그 파일 확인
dir DuplicateLogs

# 로그 파일 내용 확인 (JSON 형식)
type DuplicateLogs\TEST_TABLE_20250101_duplicates.log
```

## 성능 튜닝

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

### "ORA-00001: unique constraint violated" (중복 키 오류)
- **자동 처리**: 프로그램이 자동으로 해당 행을 Skip하고 계속 진행
- **로그 확인**: `DuplicateLogs` 폴더에서 중복 데이터 확인
- **Excel 확인**: T열에서 중복 건너뛴 행 수 확인
- **근본 원인 분석**:
  1. SQL Server와 Oracle의 Primary Key 정의 불일치
  2. INSDTTM 같은 시간 컬럼의 정밀도 차이
  3. 정렬 컬럼 (S열) 누락 또는 불완전

### "Primary Key 조회 실패" 경고
- **증상**: 콘솔에 "Primary Key 조회 실패" 메시지 출력
- **영향**: ORDER BY 1 사용으로 배치 간 중복 위험
- **해결 방법**:
  1. Excel S열에 수동으로 정렬 컬럼 지정 (예: `HIST_SEQNO, INSDTTM, ZONEID`)
  2. SQL Server 테이블에 Primary Key 정의 추가
  3. 정렬이 중요하지 않은 테이블은 무시

### Excel 파일 업데이트 실패
- **증상**: "파일이 다른 프로세스에서 사용 중" 오류
- **해결 방법**:
  1. Excel 파일을 닫고 다시 실행
  2. Excel 프로세스가 백그라운드에서 실행 중인지 확인 (작업 관리자)
  3. 프로그램 종료 후 Excel 파일 확인 (finally 블록에서 자동 저장)

### 배치 처리가 느려짐 또는 멈춤
- **원인**: REPEATABLEREAD 격리 수준으로 인한 잠금 대기
- **해결 방법**:
  1. SQL Server의 활성 트랜잭션 확인 및 종료
  2. 배치 크기 줄이기 (`BatchSize`: 1000 → 500)
  3. 데이터베이스 활동이 적은 시간대에 마이그레이션 실행

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
