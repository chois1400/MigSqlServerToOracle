# 동기 방식(Sync) 구현 가이드

## 📝 변경 계획

비동기 방식에서 동기 방식으로 변경하여 배치 간 중복을 완벽히 제거합니다.

---

## 🔧 구현 단계

### Step 1: 메인 MigrateTableAsync → MigrateTableSync 메서드 추가

```csharp
/// <summary>
/// 동기 방식으로 테이블을 배치 단위로 마이그레이션합니다.
/// 각 배치는 완전히 완료된 후 다음 배치가 시작되므로 중복이 발생하지 않습니다.
/// </summary>
public (int totalSuccess, int totalSkipped, long totalProcessed) MigrateTableSync(
    string sourceTable,
    string targetTable,
    string? whereCondition = null,
    Dictionary<string, string>? columnMappings = null,
    HashSet<string>? emptyToDashColumns = null,
    string? emptyValueReplacement = null,
    List<string>? additionalColumns = null,
    List<string>? additionalColumnsValues = null,
    string? orderByColumns = null)
{
    try
    {
        _logger.LogInformation($"========== 동기 방식 마이그레이션 시작 ==========");
        _logger.LogInformation($"Source: {sourceTable}, Target: {targetTable}");
        
        // 1. 전체 행 수 조회 (동기)
        long totalRows = GetRowCountSync(sourceTable, whereCondition);
        
        if (totalRows == 0)
        {
            _logger.LogWarning($"테이블 {sourceTable}에 데이터가 없습니다.");
            return (0, 0, 0);
        }
        
        _logger.LogInformation($"총 {totalRows}개 행을 {_batchSize}개씩 처리합니다.");
        
        // 2. Primary Key 조회 (동기)
        var primaryKeyColumns = GetPrimaryKeyColumnsSync(sourceTable);
        string orderByClause = "ORDER BY 1";
        
        if (primaryKeyColumns.Count > 0)
        {
            orderByClause = "ORDER BY " + string.Join(", ", primaryKeyColumns);
            _logger.LogInformation($"✓ Primary Key 조회 성공: {string.Join(", ", primaryKeyColumns)}");
        }
        else
        {
            _logger.LogWarning($"⚠ Primary Key를 찾을 수 없습니다. ORDER BY 1 사용");
        }
        
        // 3. 배치 단위로 순차 처리 (동기, 완벽한 순서 보장)
        int totalSuccess = 0;
        int totalSkipped = 0;
        long processedRows = 0;  // ← ✓ 성공한 행 수 기반 추적
        int batchNumber = 0;
        
        while (processedRows < totalRows)
        {
            batchNumber++;
            int batchSize = Math.Min(_batchSize, (int)(totalRows - processedRows));
            
            _logger.LogInformation($"\n[배치 {batchNumber}] 시작 (오프셋: {processedRows}, 크기: {batchSize})");
            
            try
            {
                // ✓ 동기 배치 마이그레이션
                var (successCount, skipCount, readCount) = MigrateBatchSync(
                    sourceTable,
                    targetTable,
                    processedRows,  // ← ✓ 이전에 **성공한** 행 수 기반 OFFSET
                    batchSize,
                    whereCondition,
                    columnMappings,
                    emptyToDashColumns,
                    emptyValueReplacement,
                    additionalColumns,
                    additionalColumnsValues,
                    orderByClause
                );
                
                totalSuccess += successCount;
                totalSkipped += skipCount;
                
                // ✓ **성공 건수**로만 다음 OFFSET 계산
                processedRows += successCount;
                
                _logger.LogInformation($"[배치 {batchNumber}] 완료 (성공: {successCount}, 중복: {skipCount}, 누적: {totalSuccess})");
                
                // 배치가 0개 읽었으면 종료 (데이터 부족)
                if (readCount == 0)
                {
                    _logger.LogWarning($"더 이상 읽을 데이터가 없습니다. 마이그레이션 종료.");
                    break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"[배치 {batchNumber}] 오류 발생: {ex.Message}");
                throw;
            }
        }
        
        _logger.LogInformation($"\n========== 마이그레이션 완료 ==========");
        _logger.LogInformation($"  총 처리: {processedRows}");
        _logger.LogInformation($"  성공: {totalSuccess}");
        _logger.LogInformation($"  중복 건너뜀: {totalSkipped}");
        _logger.LogInformation($"==========================================\n");
        
        return (totalSuccess, totalSkipped, processedRows);
    }
    catch (Exception ex)
    {
        _logger.LogError($"마이그레이션 실패: {ex.Message}");
        throw;
    }
}
```

### Step 2: MigrateBatchSync (동기 배치 처리)

```csharp
/// <summary>
/// 동기 방식으로 배치를 마이그레이션합니다.
/// 모든 작업이 완료될 때까지 블로킹되므로 배치 간 명확한 순서 보장.
/// </summary>
private (int successCount, int skipCount, int totalCount) MigrateBatchSync(
    string sourceTable,
    string targetTable,
    long offset,
    int batchSize,
    string? whereCondition = null,
    Dictionary<string, string>? columnMappings = null,
    HashSet<string>? emptyToDashColumns = null,
    string? emptyValueReplacement = null,
    List<string>? additionalColumns = null,
    List<string>? additionalColumnsValues = null,
    string? orderByClause = null)
{
    try
    {
        _logger.LogInformation($"[MigrateBatchSync] 시작 - 오프셋: {offset}, 크기: {batchSize}");
        
        // 1. 동기 방식으로 SQL Server에서 데이터 읽기
        var dataTable = ReadBatchSync(
            sourceTable,
            offset,
            batchSize,
            whereCondition,
            orderByClause);
        
        if (dataTable.Rows.Count == 0)
        {
            _logger.LogWarning($"[MigrateBatchSync] 읽은 행이 0개입니다.");
            return (0, 0, 0);
        }
        
        _logger.LogInformation($"[MigrateBatchSync] {dataTable.Rows.Count}개 행 읽음");
        
        // 2. 동기 방식으로 Oracle에 INSERT
        var (successCount, skipCount) = InsertIntoOracleSync(
            targetTable,
            dataTable,
            columnMappings,
            emptyToDashColumns,
            emptyValueReplacement,
            additionalColumns,
            additionalColumnsValues);
        
        _logger.LogInformation($"[MigrateBatchSync] 완료 (성공: {successCount}, 중복: {skipCount})");
        
        return (successCount, skipCount, dataTable.Rows.Count);
    }
    catch (Exception ex)
    {
        _logger.LogError($"[MigrateBatchSync] 오류: {ex.Message}");
        throw;
    }
}
```

### Step 3: ReadBatchSync (동기 데이터 읽기)

```csharp
/// <summary>
/// 동기 방식으로 SQL Server에서 배치 데이터를 읽습니다.
/// </summary>
private DataTable ReadBatchSync(
    string sourceTable,
    long offset,
    int batchSize,
    string? whereCondition = null,
    string? orderByClause = null)
{
    using (var sqlConnection = new SqlConnection(_sqlServerConnectionString))
    {
        // 연결 동기 오픈
        sqlConnection.Open();
        
        // SQL 쿼리 구성
        string whereClause = string.IsNullOrWhiteSpace(whereCondition)
            ? string.Empty
            : $" WHERE {whereCondition}";
        
        string orderBy = string.IsNullOrWhiteSpace(orderByClause)
            ? "ORDER BY 1"
            : orderByClause;
        
        string query = $@"
            SELECT *
            FROM {sourceTable}
            {whereClause}
            {orderBy}
            OFFSET {offset} ROWS
            FETCH NEXT {batchSize} ROWS ONLY";
        
        _logger.LogInformation($"[ReadBatchSync] 쿼리 실행: OFFSET {offset}, FETCH {batchSize}");
        
        using (var command = new SqlCommand(query, sqlConnection))
        {
            command.CommandTimeout = _commandTimeout;
            
            // 동기 실행 (ExecuteReader, 논-async)
            using (var reader = command.ExecuteReader())
            {
                var dataTable = new DataTable();
                // 동기 로드
                dataTable.Load(reader);
                
                _logger.LogInformation($"[ReadBatchSync] {dataTable.Rows.Count}개 행 로드 완료");
                return dataTable;
            }
        }
    }
}
```

### Step 4: InsertIntoOracleSync (동기 INSERT)

```csharp
/// <summary>
/// 동기 방식으로 Oracle에 데이터를 삽입합니다.
/// </summary>
private (int successCount, int skipCount) InsertIntoOracleSync(
    string tableName,
    DataTable dataTable,
    Dictionary<string, string>? columnMappings = null,
    HashSet<string>? emptyToDashColumns = null,
    string? emptyValueReplacement = null,
    List<string>? additionalColumns = null,
    List<string>? additionalColumnsValues = null)
{
    _logger.LogInformation($"[InsertIntoOracleSync] 시작 - 테이블: {tableName}, 행: {dataTable.Rows.Count}");
    
    using (var oracleConnection = new OracleConnection(_oracleConnectionString))
    {
        // 연결 동기 오픈
        oracleConnection.Open();
        
        // 트랜잭션 시작 (동기)
        using (var transaction = oracleConnection.BeginTransaction())
        {
            int successCount = 0;
            int skipCount = 0;
            
            try
            {
                foreach (DataRow row in dataTable.Rows)
                {
                    try
                    {
                        // Oracle INSERT SQL 구성
                        var (columnList, parameterList) = BuildInsertStatement(
                            row, dataTable, columnMappings, additionalColumns, additionalColumnsValues);
                        
                        string insertSql = $"INSERT INTO {tableName} ({columnList}) VALUES ({parameterList})";
                        
                        using (var insertCommand = new OracleCommand(insertSql, oracleConnection, transaction))
                        {
                            // 파라미터 바인딩
                            BindInsertParameters(insertCommand, row, dataTable, columnMappings, 
                                emptyToDashColumns, emptyValueReplacement, additionalColumnsValues);
                            
                            // 동기 실행
                            int rowsAffected = insertCommand.ExecuteNonQuery();
                            
                            if (rowsAffected > 0)
                            {
                                successCount++;
                            }
                        }
                    }
                    catch (OracleException oex) when (oex.Number == 1)  // ORA-00001
                    {
                        // 중복 키 무시
                        skipCount++;
                        LogDuplicateRow(tableName, row, dataTable);
                        _logger.LogWarning($"중복 행 건너뜀");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"INSERT 오류: {ex.Message}");
                        throw;
                    }
                }
                
                // 동기 커밋
                transaction.Commit();
                
                _logger.LogInformation($"[InsertIntoOracleSync] 커밋 완료 (성공: {successCount}, 중복: {skipCount})");
                
                return (successCount, skipCount);
            }
            catch (Exception ex)
            {
                _logger.LogError($"[InsertIntoOracleSync] 오류, 롤백 수행");
                transaction.Rollback();
                throw;
            }
        }
    }
}
```

### Step 5: 도우미 메서드 (동기 버전)

```csharp
/// <summary>
/// 동기 방식으로 행 수를 조회합니다.
/// </summary>
private long GetRowCountSync(string tableName, string? whereCondition = null)
{
    using (var connection = new SqlConnection(_sqlServerConnectionString))
    {
        connection.Open();
        
        string whereClause = string.IsNullOrWhiteSpace(whereCondition)
            ? string.Empty
            : $" WHERE {whereCondition}";
        
        string query = $"SELECT COUNT(*) FROM {tableName}{whereClause}";
        
        using (var command = new SqlCommand(query, connection))
        {
            command.CommandTimeout = _commandTimeout;
            var result = command.ExecuteScalar();
            return result != null ? Convert.ToInt64(result) : 0;
        }
    }
}

/// <summary>
/// 동기 방식으로 Primary Key 컬럼을 조회합니다.
/// </summary>
private List<string> GetPrimaryKeyColumnsSync(string tableName)
{
    var pkColumns = new List<string>();
    
    using (var connection = new SqlConnection(_sqlServerConnectionString))
    {
        connection.Open();
        
        string schemaName = "dbo";
        string tableNameOnly = tableName;
        if (tableName.Contains('.'))
        {
            var parts = tableName.Split('.');
            schemaName = parts[0];
            tableNameOnly = parts[1];
        }
        
        // INFORMATION_SCHEMA 쿼리 (동기)
        string query = @"
            SELECT c.COLUMN_NAME, c.ORDINAL_POSITION
            FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
            JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE c 
                ON tc.CONSTRAINT_NAME = c.CONSTRAINT_NAME
            WHERE tc.CONSTRAINT_TYPE = 'PRIMARY KEY'
                AND tc.TABLE_SCHEMA = @schemaName
                AND tc.TABLE_NAME = @tableName
            ORDER BY c.ORDINAL_POSITION";
        
        using (var command = new SqlCommand(query, connection))
        {
            command.CommandTimeout = _commandTimeout;
            command.Parameters.AddWithValue("@schemaName", schemaName);
            command.Parameters.AddWithValue("@tableName", tableNameOnly);
            
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    pkColumns.Add(reader.GetString(0));
                }
            }
        }
        
        return pkColumns;
    }
}
```

---

## 📋 Program.cs 수정

기존 비동기 호출을 동기로 변경:

```csharp
// 변경 전
await migrationService.MigrateTableAsync(sourceTable, targetTable);

// 변경 후
var (success, skip, total) = migrationService.MigrateTableSync(
    sourceTable,
    targetTable,
    whereCondition,
    columnMappings,
    emptyToDashColumns,
    emptyValueReplacement,
    additionalColumns,
    additionalColumnsValues,
    orderByColumns);

_logger.LogInformation($"마이그레이션 결과: 성공 {success}, 중복 {skip}");
```

---

## ✅ 변경의 이점

| 항목 | 비동기 (현재) | 동기 (제안) |
|------|-------------|----------|
| **배치 간 순서** | ❌ 보장 안 함 | ✅ 완벽히 보장 |
| **중복 위험** | ⚠️ 높음 | ✅ 0건 |
| **성능** | ~150초 | ~150초 (동일) |
| **코드 복잡도** | 높음 (Task 관리) | 낮음 (순차 처리) |
| **디버깅** | 어려움 | 쉬움 |

---

## 🧪 테스트 시나리오

```csharp
[Test]
public void TestSyncMigrationNoDuplicates()
{
    // 1단계: 100,000개 행 준비
    InsertTestDataToSqlServer(100000);
    
    // 2단계: 동기 방식 마이그레이션
    var (success, skip, total) = migrationService.MigrateTableSync(
        "dbo.TestTable",
        "TEST_TABLE");
    
    // 3단계: 검증
    Assert.AreEqual(100000, success, "모든 행이 INSERT되어야 함");
    Assert.AreEqual(0, skip, "중복이 없어야 함");
    Assert.AreEqual(100000, GetOracleTableCount("TEST_TABLE"), "Oracle 행 수 일치");
}
```

---

## 🚀 마이그레이션 계획

1. **Phase 1**: 동기 메서드 추가 (기존 비동기 유지)
2. **Phase 2**: 테스트 (작은 데이터셋)
3. **Phase 3**: 성능 검증
4. **Phase 4**: 프로덕션 적용
5. **Phase 5**: 기존 비동기 메서드 제거 (선택사항)
