# SQL Server to Oracle Migration Tool

이 프로그램은 SQL Server 데이터베이스에서 Oracle 데이터베이스로 데이터를 이전하는 .NET Core 콘솔 애플리케이션입니다.

## 기능

- ✅ SQL Server에서 배치 단위로 데이터 읽기 (REPEATABLEREAD 격리 수준)
- ✅ 데이터 타입 자동 매핑 (SQL Server → Oracle)
- ✅ 트랜잭션 기반 데이터 삽입 (배치별)
- ✅ **Excel 매핑 파일을 사용한 테이블 매핑**
- ✅ **Primary Key 기반 자동 정렬** (배치 중복 방지)
- ✅ **중복 키 자동 Skip** (ORA-00001 처리)
- ✅ **중복 데이터 로그 파일 생성** (DuplicateLogs 폴더)
- ✅ **마이그레이션 통계 Excel 기록** (성공/중복/전체 건수, 시간 정보)
- ✅ **Oracle 함수식 지원** (L열: TO_CHAR, SYSDATE 등)
- ✅ **배치 내 중복 검증 및 경고**
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

### 2. 마이그레이션 방법 선택

#### 방법 1: 수동으로 테이블 지정 (간단한 마이그레이션)

`Program.cs`에서 다음 섹션을 수정하여 마이그레이션할 테이블을 지정하세요:

```csharp
// 특정 테이블 마이그레이션
string sourceTable = "dbo.YourTableName";
string targetTable = "YOUR_TABLE_NAME";  // Oracle 테이블명 (일반적으로 대문자)

logger.LogInformation($"Starting migration of table '{sourceTable}'");
await migrationService.MigrateTableAsync(sourceTable, targetTable);
logger.LogInformation("Migration completed successfully");
```

#### 방법 2: Excel 매핑 파일 사용 (권장 - 복잡한 마이그레이션)

SQL Server와 Oracle의 테이블명이 다른 경우, **Excel 파일을 사용하여 테이블 매핑을 관리**할 수 있습니다.

**단계 1: 샘플 Excel 매핑 파일 생성**

`Program.cs`의 "예제 3" 섹션에서 다음 코드를 주석 해제:

```csharp
string mappingFilePath = Path.Combine(Directory.GetCurrentDirectory(), "TableMapping.xlsx");
logger.LogInformation("샘플 Excel 매핑 파일 생성 중...");
mappingReader.CreateSampleMappingFile(mappingFilePath);
```

프로그램을 실행하면 `TableMapping.xlsx` 파일이 생성됩니다.

**단계 2: Excel 파일 편집**

생성된 `TableMapping.xlsx` 파일을 열고 다음과 같이 작성하세요:

| SQL Server 테이블명 | Oracle 테이블명 | 활성화 | 설명 | WhereCondition | TruncateTarget |
|-------------------|----------------|-------|------|----------------|----------------|
| dbo.Employees | EMPLOYEES | TRUE | 직원 정보 | IsActive = 1 | TRUE |
| dbo.Departments | DEPARTMENTS | TRUE | 부서 정보 | | FALSE |
| dbo.Projects | PROJECTS | FALSE | 현재 제외 | Status = 'Completed' | FALSE |

- **A열**: SQL Server 테이블명 (스키마 포함, 예: `dbo.TableName`)
- **B열**: Oracle 테이블명 (일반적으로 대문자)
- **C열**: 활성화 여부 (`TRUE`/`FALSE`, 기본값: `TRUE`)
- **D열**: 설명 (선택사항)
- **E열**: WHERE 조건 (선택사항, SQL Server에서 데이터 추출 시 사용할 WHERE 절)
  - 예: `IsActive = 1`, `Region = 'KR'`, `HireDate > '2023-01-01'`
  - 빈 값이면 전체 데이터 추출
- **F열**: TruncateTarget (선택사항, Oracle 대상 테이블 사전 초기화)
  - `TRUE` 값이면 마이그레이션 전에 Oracle 테이블의 기존 데이터를 삭제(DELETE FROM)
  - 기본값: `FALSE`
  - 허용 값: `TRUE`, `YES`, `1`, `O`, `삭제`, `TRUNCATE`
- **G열**: SQL Server 컬럼명 목록 (선택사항, 쉼표로 구분)
  - 예: `EmployeeID,EmployeeName,HireDate`
  - 빈 값이면 SQL Server 테이블의 모든 컬럼을 Oracle 테이블의 동일 이름 컬럼에 매핑
- **H열**: Oracle 컬럼명 목록 (선택사항, 쉼표로 구분)
  - 예: `EMP_ID,EMP_NAME,HIRE_DT`
  - G열과 같은 개수의 컬럼명을 쉼표로 구분하여 입력
  - G열과 H열이 모두 입력되면 해당 매핑이 적용됨
- **V열**: Oracle PK Columns (중복 체크용 대상 PK 컬럼)
  - 예: `ZONEID,TRANSACTION_SERIAL_NO`
  - 이 열에 지정된 컬럼들은 Oracle의 대상 테이블에서 중복 존재 여부를 확인하는 WHERE의 키로 "반드시 모두" 사용됩니다.
  - 비워두면, 프로그램이 Oracle 메타데이터(OWNER+TABLE 기준)에서 Primary Key 전체 컬럼을 자동 추출하여 사용합니다.
  - H열(Oracle 컬럼명)과 달리, V열은 "중복 조회 및 MERGE ON" 키 결정에만 사용되며 INSERT 컬럼과 무관하게 전체 PK를 강제 포함합니다.
- **I열**: EmptyToDashColumns - 공백값을 '-'로 대체할 SQL Server 컬럼명 목록 (선택사항, 쉼표로 구분)
  - 예: `EmployeeName,Address`
  - SQL Server의 해당 컬럼 값이 공백(또는 공백만 포함)인 경우 Oracle에 `-`로 저장됨
  - NOT NULL 컬럼인데 SQL Server에는 공백이, Oracle에는 공백이 NULL로 치환되는 문제를 해결하기 위함
  - 빈 값이면 이 변환을 적용하지 않음
- **J열**: EmptyValueReplacement (선택사항, 기본값: `-`)
  - I열에서 지정한 컬럼들의 공백값을 대체할 값을 지정합니다
  - 예: `-`, `N/A`, `UNKNOWN` 등
  - 빈 값이면 기본값 `-`을 사용
- **K열**: AdditionalColumns - Oracle 테이블에만 존재하는 추가 컬럼명 (선택사항, 쉼표로 구분)
  - 예: `CreatedDate,UpdatedDate,IsDeleted`
  - SQL Server에는 없지만 Oracle 테이블에는 있는 컬럼들을 지정
  - 공 값이면 추가 컬럼이 없는 것으로 처리
- **L열**: AdditionalColumnsValues (선택사항, 쉼마로 구분)
  - K열의 각 추가 컬럼에 대한 값 또는 함수식을 지정합니다
  - K열과 L열의 개수가 일치해야 합니다
  - **지원하는 값**:
    - **고정값**: `'2024-01-01'`, `'Y'`, `'0'` (따옴표로 감싼 문자열)
    - **Oracle 함수**: `SYSDATE`, `SYSTIMESTAMP`, `CURRENT_TIMESTAMP`, `CURRENT_DATE`
    - **동적값**: `{ColumnName}` (해당 SQL Server 컬럼값으로 치환)
      - 예: `{EmployeeID}` - SQL Server의 EmployeeID 컬럼값을 사용
    - **조합 함수 / Oracle 함수 사용 예 (권장 표기)**:
      - `SUBSTR({ColumnName}, 1, 5)`
      - `TO_CHAR({INSDTTM}, 'YYYYMMDDHH24MISSFF9')`  ← Oracle에서 평가되도록 `{}`로 컬럼을 감싸서 사용
      - 주의: L열의 식은 그대로 Oracle INSERT VALUES 절에 삽입되어 실행되므로, 소스 컬럼명을 `{ColumnName}` 형태로 표기하거나 SELECT 결과에 해당 컬럼이 포함되어 있어야 합니다.
      - 예제 설명: K열에 추가 컬럼 `FormattedDt`를 넣고, L열에 `TO_CHAR({INSDTTM}, 'YYYYMMDDHH24MISSFF9')`를 입력하면 Oracle에서 `TO_CHAR(:pX, '...')` 형태로 치환되어 함수 결과가 삽입됩니다.
  - 예: `SYSDATE,'ACTIVE',{EmployeeID}` → 3개 추가 컬럼에 현재시간, 문자값, 직원ID를 각각 입력

**단계 3: 매핑 기반 마이그레이션 실행**

`Program.cs`의 "예제 3" 섹션에서 다음 코드를 주석 해제:

```csharp
string mappingFile = "TableMapping.xlsx";
if (File.Exists(mappingFile))
{
    var mappings = mappingReader.ReadMappingsFromExcel(mappingFile);
    // 방법 1: 매핑 정보만 사용하여 마이그레이션
    await migrationService.MigrateWithMappingAsync(mappings);
    
    // 방법 2: 모든 테이블의 데이터를 먼저 삭제하고 마이그레이션
    // await migrationService.MigrateTablesFromMappingAsync(mappings, truncateFirst: true);
}
```

프로그램을 실행하면:
- **활성화된 테이블만** 마이그레이션 수행
- **TruncateTarget = TRUE인 테이블**: 마이그레이션 전에 Oracle 테이블 초기화
- **TruncateTarget = TRUE인 테이블**: 마이그레이션 전에 Oracle 테이블 초기화 (DELETE FROM)
- **WhereCondition이 설정된 테이블**: 해당 WHERE 조건으로 필터링된 데이터만 추출

## 사용 방법

### 프로젝트 빌드

```bash
dotnet build
```

### 프로그램 실행

```bash
dotnet run
```

### 짧은 형태(명령줄 인수)

```powershell
dotnet run -- -c appsettings.json -m TableMapping.xlsx
```

또는 전체 경로 지정:

```powershell
dotnet run -- --config "C:\path\to\appsettings.json" --mapping "C:\path\to\TableMapping.xlsx"
```

### 디버그 모드 실행

```bash
dotnet run --configuration Debug
```

## 주요 클래스

### MigrationService

- **GetSourceTablesAsync()**: SQL Server의 모든 테이블 목록 조회
- **MigrateTableAsync(sourceTable, targetTable)**: 특정 테이블 마이그레이션 (배치 처리)
- **MigrateWithMappingAsync(mappings)**: Excel 매핑 기반 마이그레이션 (NEW)
- **DeleteOracleTableAsync(tableName)**: Oracle 테이블 데이터 삭제

### TableMappingReader

- **ReadMappingsFromExcel(filePath)**: Excel 파일에서 테이블 매핑 정보 읽기
- **CreateSampleMappingFile(filePath)**: 샘플 Excel 매핑 파일 생성

### TableMapping

- **SqlServerTableName**: SQL Server 테이블명
- **OracleTableName**: Oracle 테이블명
- **IsActive**: 마이그레이션 활성화 여부
- **Description**: 설명
- **WhereCondition**: WHERE 절 (예: "IsActive = 1") - SQL Server에서 선택적으로 특정 행만 추출
- **DeleteTarget**: Oracle 테이블 초기화 여부 - TRUE이면 마이그레이션 전에 대상 테이블의 데이터를 삭제(DELETE FROM)
- **ColumnMappings**: SQL Server 컬럼명 → Oracle 컬럼명 매핑 (Dictionary)
- **EmptyToDashColumns**: 공백값을 대체값으로 변환할 SQL Server 컬럼명 목록 (HashSet)
- **EmptyValueReplacement**: 공백값을 대체할 문자열 (기본값: `-`)
- **AdditionalColumns**: Oracle 테이블에만 존재하는 추가 컬럼명 목록 (List<string>)
- **AdditionalColumnsValues**: 추가 컬럼에 입력할 값 또는 함수식 목록 (List<string>)
- **OrderByColumns**: 정렬 기준 컬럼 (S열, 선택사항) - 배치 중복 방지용
- **StartTime/EndTime**: 마이그레이션 시작/완료 시간 (M/N열, 자동 기록)
- **Status**: 마이그레이션 상태 (O열: 완료/실패/진행 중, 자동 기록)
- **RecordCount**: 성공한 레코드 수 (Q열, 자동 기록)
- **SkippedCount**: 중복으로 건너뛴 레코드 수 (T열, 자동 기록)
- **TotalProcessed**: 전체 처리 레코드 수 (U열, 자동 기록)
- **ErrorMessage**: 오류 메시지 (R열, 실패 시 자동 기록)

## 배치 처리 방식

- 기본 배치 크기: 1,000행 (appsettings.json에서 조정 가능)
- 각 배치는 별도의 트랜잭션으로 처리
- 배치 실패 시 해당 배치 트랜잭션만 롤백

## Excel 매핑 파일 상세 설명

### 컬럼별 동작

| 컬럼 | 이름 | 설명 | 기본값 | 예제 |
|------|------|------|--------|------|
| A | SQL Server 테이블명 | 마이그레이션할 SQL Server의 테이블명 (스키마 포함) | 필수 | `dbo.Employees` |
| B | Oracle 테이블명 | 데이터를 받을 Oracle의 테이블명 | 필수 | `EMPLOYEES` |
| C | 활성화 | TRUE이면 해당 테이블을 마이그레이션, FALSE이면 건너뜀 | TRUE | TRUE, FALSE |
| D | 설명 | 테이블 마이그레이션에 대한 설명 (로그에만 표시) | 선택 | "직원 정보 테이블" |
| E | WhereCondition | SQL Server에서 데이터를 추출할 때 적용할 WHERE 조건 | 선택 (전체 추출) | `IsActive = 1`, `HireDate > '2023-01-01'` |
| F | TruncateTarget | TRUE이면 마이그레이션 전에 Oracle 테이블의 기존 데이터 삭제 | FALSE | TRUE, FALSE |
| G | SQL Server 컬럼명 | SQL Server의 컬럼명들 (쉼표로 구분) | 선택 (전체 컬럼) | `EmployeeID,EmployeeName,Department` |
| H | Oracle 컬럼명 | Oracle의 컬럼명들 (쉼표로 구분, G열과 개수 일치) | 선택 | `EMP_ID,EMP_NAME,DEPT` |
| I | EmptyToDashColumns | 공백값을 대체값으로 변환할 컬럼명 (쉼표로 구분) | 선택 (변환 안 함) | `EmployeeName,Department` |
| J | EmptyValueReplacement | 공백값을 대체할 문자열 | `-` | `-`, `N/A`, `UNKNOWN` |
| K | AdditionalColumns | Oracle 전용 추가 컬럼명 (쉼표로 구분) | 선택 (추가 컬럼 없음) | `CreatedDate,UpdatedDate,IsDeleted` |
| L | AdditionalColumnsValues | 추가 컬럼의 값 또는 함수 (쉼표로 구분, K열과 개수 일치) | 선택 | `SYSDATE,'Y',TO_CHAR({INSDTTM},'YYYYMMDD')` |
| M | 시작 시간 | 마이그레이션 시작 시각 | 자동 기록 | 2025-12-10 10:30:00 |
| N | 완료 시간 | 마이그레이션 완료 시각 | 자동 기록 | 2025-12-10 10:35:00 |
| O | 상태 | 마이그레이션 상태 | 자동 기록 | 완료, 실패, 진행 중 |
| P | 소요 시간 | 마이그레이션 소요 시간 | 자동 계산 | 300초 |
| Q | 이전 레코드 수 | 성공한 레코드 수 | 자동 기록 | 10000 |
| R | 오류 메시지 | 실패 시 오류 내용 | 자동 기록 | ORA-00001... |
| S | 정렬 컬럼 | 배치 정렬 기준 (중복 방지용) | 선택 (PK 자동 조회) | `HIST_SEQNO, INSDTTM, ZONEID` |
| T | 중복 건너뜀 | 중복으로 Skip된 레코드 수 | 자동 기록 | 50 |
| U | 전체 처리 | SQL Server에서 읽은 전체 레코드 수 | 자동 기록 | 10050 |

### TRANSACTION_SERIAL_NO 구성 (중복 회피용)
- 목적: Oracle 테이블에 `TRANSACTION_SERIAL_NO (VARCHAR2(50))`가 있을 때, SQL Server의 `INSDTTM`과 `HIST_SEQNO` 조합으로 고유 식별자를 생성하여 중복을 회피합니다.
- 설정 예시:
  - K열: `TRANSACTION_SERIAL_NO`
  - L열: `TO_CHAR({INSDTTM}, 'YYYYMMDDHH24MISSFF9') || LPAD(TO_CHAR({HIST_SEQNO}), 12, '0')`
    - 변형: `NVL(LPAD(TO_CHAR({HIST_SEQNO}), 12, '0'), '')`로 널 방어 가능
    - 구분자 필요 시: `|| '_' ||` 등 추가 가능
- 중요:
  - `HIST_SEQNO`는 H열(타깃 매핑)에 없어도 됩니다. L열의 `{HIST_SEQNO}` 토큰은 자동으로 `:pN` 파라미터가 생성되어 값이 바인딩됩니다.
  - `TRANSACTION_SERIAL_NO`는 G/H 매핑에 넣지 말고 K/L로만 추가하세요. 동일 타깃 컬럼이 두 번 나오면 ORA-00957(duplicate column name)이 발생합니다.

### PK 전체 컬럼으로 중복 조회 (V열)
- V열에 Oracle PK 전체 컬럼을 입력하면, 중복 검증과 로그의 `ExistingOracleSelect`가 반드시 모든 PK를 포함합니다.
- 비워둘 경우 OWNER+TABLE 기준으로 Oracle 메타데이터에서 PK 전체 컬럼을 자동 추출하여 사용합니다.

### ORA-01006(bind variable does not exist) 방지
- 원인: L열 식의 `{Col}` 토큰이 파라미터로 치환되지 않거나 이름/순서 불일치.
- 해결:
  - L열의 `{INSDTTM}`, `{HIST_SEQNO}` 등 토큰은 자동으로 `:pN`으로 치환되고 값이 바인딩됩니다(매핑에 없어도 OK).
  - 내부적으로 `BindByName = true`로 이름 바인딩을 강제합니다.
  - 동일 타깃 컬럼의 중복(예: G/H에 `TRANSACTION_SERIAL_NO` + K에도 `TRANSACTION_SERIAL_NO`)을 피하세요.

### 사용 시나리오

**시나리오 1: 신규 데이터 마이그레이션 (기존 데이터 덮어쓰기)**
- TruncateTarget = TRUE
- WhereCondition = 비워둠 (전체 데이터)
- 결과: 기존 Oracle 데이터 삭제 후 SQL Server 데이터 전체 삽입

**시나리오 2: 부분 데이터 추출 (조건부 마이그레이션)**
- TruncateTarget = FALSE
- WhereCondition = `IsActive = 1`
- 결과: SQL Server에서 활성화된 데이터만 선택적으로 추출하여 Oracle에 추가

**시나리오 3: 증분 마이그레이션 (기존 데이터 보존)**
- TruncateTarget = FALSE
- WhereCondition = `CreatedDate >= CAST(GETDATE() AS DATE)`
- 결과: 오늘 생성된 데이터만 Oracle에 추가

**시나리오 4: 특정 테이블만 마이그레이션 제외**
- 활성화 = FALSE
- 결과: 해당 테이블은 건너뜀

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
await migrationService.DeleteOracleTableAsync("YOUR_TABLE_NAME");
```

## 로깅

프로그램은 다음 정보를 콘솔에 출력합니다:

- 마이그레이션 시작/종료
- 배치별 진행 상황
- 오류 및 예외 메시지
- 총 마이그레이션 행 수
- Excel 매핑 파일 읽기 결과

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

### Excel 파일 오류
- Excel 파일이 올바른 경로에 있는지 확인하세요
- Excel 파일이 손상되지 않았는지 확인하세요
- A열(SQL Server 테이블명)과 B열(Oracle 테이블명)이 비어있지 않은지 확인하세요

## 라이센스

MIT License

## 기여

개선 사항이나 버그 리포트는 이슈로 등록해주세요.


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
- **MigrateWithMappingAsync(mappings)**: Excel 매핑을 사용한 다중 테이블 마이그레이션
- **DeleteOracleTableAsync(tableName)**: Oracle 테이블 데이터 삭제
- **GetPrimaryKeyColumnsAsync(tableName)**: SQL Server 테이블의 Primary Key 컬럼 조회
- **InsertIntoOracleAsync(...)**: Oracle에 데이터 삽입 (중복 자동 Skip)

## 배치 처리 방식

- 기본 배치 크기: 1,000행 (appsettings.json에서 조정 가능)
- 각 배치는 별도의 트랜잭션으로 처리
- **REPEATABLEREAD 격리 수준 사용** - 배치 간 데이터 중복 방지
- **Primary Key 기반 자동 정렬** - 배치마다 일관된 순서 보장
- **중복 키 자동 Skip** - ORA-00001 발생 시 해당 행만 건너뛰고 계속 진행
- 배치 실패 시 해당 배치 트랜잭션만 롤백

## 중복 데이터 처리

### 중복 발생 시 동작
1. Oracle INSERT 시 ORA-00001 (unique constraint violated) 발생
2. 해당 행을 자동으로 Skip하고 다음 행 처리 계속
3. 중복된 데이터를 `DuplicateLogs` 폴더에 JSON 형식으로 기록
4. 중복 건수를 Excel T열에 자동 기록

### 중복 로그 파일
- **위치**: `프로젝트폴더/DuplicateLogs/`
- **파일명**: `테이블명_YYYYMMDD_duplicates.log`
- **형식**: JSON Lines (한 줄에 하나의 JSON 객체)
- **내용**: 
  - Keys: 중복 판단에 사용된 키 구성(Target/Source/Value)
  - ExistingOracleWhere: Oracle에서 기존 행 조회에 사용된 WHERE (전체 PK 반영)
  - ExistingOracleSelect: 위 WHERE로 만드는 SELECT 문 (최대 5행 미리보기)
  - AttemptedInsertSql: 실제로 실행하려던 INSERT/MERGE SQL
  - AttemptedInsertParams: INSERT/MERGE에 바인딩된 파라미터 스냅샷
  - ResolvedOraclePkTargets: 최종적으로 사용된 Oracle PK 타깃 컬럼 목록
  - ResolvedSourceCols: 각 타깃 컬럼에 대해 소스에서 추출된 컬럼명(가능한 경우)
  - ConstraintName: ORA-00001에 포함된 제약조건명(있다면)
  - Timestamp: 중복 발생 시각
  - Table: 테이블명
  - Data: SQL Server에서 읽은 모든 컬럼 데이터
  - Analysis: INSDTTM 정밀도 분석 (해당되는 경우)

### 배치 중복 방지
- **Excel S열**: 수동으로 정렬 기준 컬럼 지정 (예: `HIST_SEQNO, INSDTTM, ZONEID`)
- **S열이 비어있으면**: Primary Key를 자동 조회하여 정렬
- **PK 조회 실패 시**: 경고 후 ORDER BY 1 사용 (중복 위험 있음)

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
await migrationService.DeleteOracleTableAsync("YOUR_TABLE_NAME");
```

## 로깅

프로그램은 다음 정보를 콘솔에 출력합니다:

- 마이그레이션 시작/종료
- 배치별 진행 상황 (전체 건수, 성공 건수, 중복 건수)
- Primary Key 조회 결과
- 배치 내부 중복 감지 경고
- 중복 데이터 상세 정보 (처음 5건)
- 오류 및 예외 메시지
- 총 마이그레이션 행 수

### 중복 데이터 로그 파일
- 위치: `DuplicateLogs/` 폴더
- 파일명: `{테이블명}_{YYYYMMDD}_duplicates.log`
- 내용: JSON 형식의 중복 행 데이터 및 분석 정보

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

### 중복 데이터 오류 (ORA-00001)
- **증상**: Oracle INSERT 시 Unique constraint violated 오류 발생
- **해결 방법**:
  1. `DuplicateLogs` 폴더에서 중복 로그 파일 확인
  2. Excel S열에 적절한 정렬 컬럼 지정 (예: Primary Key 컬럼들)
  3. SQL Server와 Oracle의 Primary Key 정의가 일치하는지 확인
  4. INSDTTM 같은 시간 컬럼을 Oracle PK로 사용 시 정밀도 확인
  5. 로그에서 "Primary Key 조회 실패" 경고 확인 → S열에 수동으로 정렬 컬럼 지정

### 배치 처리 느림
- `appsettings.json`의 `BatchSize`를 증가시키세요 (기본값: 1000)
- Oracle 테이블의 인덱스를 임시 비활성화하세요
- `REPEATABLEREAD` 격리 수준이 필요한지 검토하세요 (데이터 일관성 vs 성능)

## 라이센스

MIT License

## 기여

개선 사항이나 버그 리포트는 이슈로 등록해주세요.
