# SQL Server to Oracle Migration Tool

이 프로그램은 SQL Server 데이터베이스에서 Oracle 데이터베이스로 데이터를 이전하는 .NET Core 콘솔 애플리케이션입니다.

## 기능

- ✅ SQL Server에서 배치 단위로 데이터 읽기
- ✅ 데이터 타입 자동 매핑 (SQL Server → Oracle)
- ✅ 트랜잭션 기반 데이터 삽입 (배치별)
- ✅ **Excel 매핑 파일을 사용한 테이블 매핑** (새 기능)
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
- **I열**: EmptyToDashColumns - 공백값을 '-'로 대체할 SQL Server 컬럼명 목록 (선택사항, 쉼표로 구분)
  - 예: `EmployeeName,Address`
  - SQL Server의 해당 컬럼 값이 공백(또는 공백만 포함)인 경우 Oracle에 `-`로 저장됨
  - NOT NULL 컬럼인데 SQL Server에는 공백이, Oracle에는 공백이 NULL로 치환되는 문제를 해결하기 위함
  - 빈 값이면 이 변환을 적용하지 않음
- **J열**: EmptyValueReplacement (선택사항, 기본값: `-`)
  - I열에서 지정한 컬럼들의 공백값을 대체할 값을 지정합니다
  - 예: `-`, `N/A`, `UNKNOWN` 등
  - 빈 값이면 기본값 `-`을 사용

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
- **DeleteOracleTableAsync(tableName)**: Oracle 테이블 데이터 삭제

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
await migrationService.DeleteOracleTableAsync("YOUR_TABLE_NAME");
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
