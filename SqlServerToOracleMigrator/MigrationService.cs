using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Oracle.ManagedDataAccess.Client;
using Dapper;
using System.Data;
using System.Text;
using System.Text.Json;

namespace SqlServerToOracleMigrator;

/// <summary>
/// Handles data migration from SQL Server to Oracle.
/// Reads data in batches from SQL Server and inserts into Oracle with error handling.
/// </summary>
public class MigrationService
{
    private readonly string _sqlServerConnectionString;
    private readonly string _oracleConnectionString;
    private readonly int _batchSize;
    private readonly int _commandTimeout;
    private readonly ILogger<MigrationService> _logger;
    private readonly string _duplicateLogDirectory;

    public MigrationService(
        string sqlServerConnectionString,
        string oracleConnectionString,
        int batchSize,
        int commandTimeout,
        ILogger<MigrationService> logger)
    {
        _sqlServerConnectionString = sqlServerConnectionString;
        _oracleConnectionString = oracleConnectionString;
        _batchSize = batchSize;
        _commandTimeout = commandTimeout;
        _logger = logger;
        
        // 중복 데이터 로그를 저장할 디렉토리 설정
        _duplicateLogDirectory = Path.Combine(Directory.GetCurrentDirectory(), "DuplicateLogs");
        if (!Directory.Exists(_duplicateLogDirectory))
        {
            Directory.CreateDirectory(_duplicateLogDirectory);
            _logger.LogInformation($"중복 데이터 로그 디렉토리 생성: {_duplicateLogDirectory}");
        }
    }
    
    /// <summary>
    /// 중복으로 건너뛴 데이터를 로그 파일에 기록합니다.
    /// </summary>
    private void LogDuplicateRow(string tableName, DataRow row, DataTable dataTable)
    {
        try
        {
            // 파일명: 테이블명_날짜.log
            string timestamp = DateTime.Now.ToString("yyyyMMdd");
            string logFileName = $"{tableName.Replace(".", "_")}_{timestamp}_duplicates.log";
            string logFilePath = Path.Combine(_duplicateLogDirectory, logFileName);
            
            // 중복 데이터를 JSON 형식으로 변환
            var rowData = new Dictionary<string, object?>();
            foreach (DataColumn column in dataTable.Columns)
            {
                var value = row[column];
                rowData[column.ColumnName] = value == DBNull.Value ? null : value;
            }
            
            // INSDTTM 값의 정밀도 확인 (문제 진단용)
            string? insdttmAnalysis = null;
            if (dataTable.Columns.Contains("INSDTTM") && row["INSDTTM"] != DBNull.Value)
            {
                var insdttmValue = row["INSDTTM"];
                if (insdttmValue is DateTime dt)
                {
                    // DATETIME2(7)의 정밀도까지 표시
                    insdttmAnalysis = $"INSDTTM Raw: {dt:yyyy-MM-dd HH:mm:ss.fffffff}";
                    // TO_CHAR 결과 시뮬레이션
                    var toCharResult = dt.ToString("yyyyMMddHHmmssffffff") + "00"; // F9 시뮬레이션 (9자리)
                    insdttmAnalysis += $" | TO_CHAR 시뮬레이션: {toCharResult}";
                }
            }
            
            // 로그 엔트리 생성
            var logEntry = new
            {
                Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                Table = tableName,
                Data = rowData,
                Analysis = insdttmAnalysis,
                Warning = "Oracle PK 충돌 - TRANSACTION_SERIAL_NO가 동일한 다른 행이 이미 존재할 가능성"
            };
            
            string jsonLine = JsonSerializer.Serialize(logEntry, new JsonSerializerOptions 
            { 
                WriteIndented = false,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
            
            // 파일에 추가 (동기화된 방식으로 여러 스레드 안전)
            lock (this)
            {
                File.AppendAllText(logFilePath, jsonLine + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"중복 데이터 로그 기록 실패: {ex.Message}");
        }
    }

    /// <summary>
    /// Migrates a specific table from SQL Server to Oracle.
    /// If a whereCondition is provided, it will be used in the SELECT statement to filter rows.
    /// If columnMappings is provided, column names will be mapped during INSERT.
    /// </summary>
    public async Task<(int successCount, int skippedCount, int totalProcessed)> MigrateTableAsync(string sourceTable, string targetTable, string? whereCondition = null, Dictionary<string, string>? columnMappings = null, HashSet<string>? emptyToDashColumns = null, string? emptyValueReplacement = null, List<string>? additionalColumns = null, List<string>? additionalColumnsValues = null, string? orderByColumns = null)
    {
        try
        {
            _logger.LogInformation($"Starting migration of table '{sourceTable}' -> '{targetTable}'");
            if (columnMappings?.Count > 0)
            {
                _logger.LogInformation($"  Column mappings: {columnMappings.Count} columns mapped");
            }
            if (additionalColumns?.Count > 0)
            {
                _logger.LogInformation($"  Additional columns: {additionalColumns.Count} columns");
            }
            // Get row count
            long totalRows = await GetRowCountAsync(sourceTable, whereCondition);
            _logger.LogInformation($"Total rows to migrate: {totalRows}");

            if (totalRows == 0)
            {
                _logger.LogWarning($"Table '{sourceTable}' is empty. Skipping migration.");
                return (0, 0, 0);
            }

            // Get ORDER BY clause for consistent ordering across all batches
            _logger.LogInformation($"==========================================");
            _logger.LogInformation($"ORDER BY 절 결정 중...");
            string orderByClause;
            
            // 1. Excel에서 명시적으로 지정한 정렬 컬럼 사용 (최우선)
            if (!string.IsNullOrWhiteSpace(orderByColumns))
            {
                orderByClause = $"ORDER BY {orderByColumns}";
                _logger.LogInformation($"✓ Excel S열에서 지정한 정렬 컬럼 사용: {orderByColumns}");
                _logger.LogInformation($"✓ ORDER BY 절: {orderByClause}");
            }
            // 2. Primary Key 자동 조회
            else
            {
                _logger.LogInformation($"Excel S열이 비어있음. Primary Key 자동 조회 시작...");
                var primaryKeyColumns = await GetPrimaryKeyColumnsAsync(sourceTable);
                
                if (primaryKeyColumns.Count > 0)
                {
                    orderByClause = $"ORDER BY {string.Join(", ", primaryKeyColumns)}";
                    _logger.LogInformation($"✓ Primary Key 조회 성공 (컬럼 수: {primaryKeyColumns.Count})");
                    foreach (var pkCol in primaryKeyColumns)
                    {
                        _logger.LogInformation($"  - PK 컬럼: {pkCol}");
                    }
                    _logger.LogInformation($"✓ ORDER BY 절: {orderByClause}");
                    
                    // 복합키 경고
                    if (primaryKeyColumns.Count > 1)
                    {
                        _logger.LogWarning($"⚠️ 복합 Primary Key 감지 ({primaryKeyColumns.Count}개 컬럼)");
                        _logger.LogWarning($"⚠️ 배치 정렬 일관성을 위해 모든 PK 컬럼을 ORDER BY에 사용합니다");
                        _logger.LogWarning($"⚠️ 순서: {string.Join(" → ", primaryKeyColumns)}");
                    }
                }
                else
                {
                    _logger.LogError($"✗✗✗ 심각한 오류: Primary Key를 찾을 수 없습니다! ✗✗✗");
                    _logger.LogError($"✗ 기본 정렬(ORDER BY 1) 사용 - 중복 데이터 문제 발생 가능성 매우 높음!");
                    orderByClause = "ORDER BY 1";
                }
            }
            _logger.LogInformation($"==========================================");
            
            // 로그 즉시 출력
            Console.Out.Flush();

            // Migrate in batches
            long migratedRows = 0;
            int batchNumber = 0;
            int totalSuccess = 0;
            int totalSkipped = 0;
            int totalProcessed = 0;

            while (migratedRows < totalRows)
            {
                batchNumber++;
                int offset = (int)migratedRows;
                int currentBatchSize = Math.Min(_batchSize, (int)(totalRows - migratedRows));

                _logger.LogInformation($"Processing batch {batchNumber}: offset={offset}, size={currentBatchSize}");
                
                if (batchNumber == 1)
                {
                    _logger.LogInformation($"[배치 1] 사용 중인 ORDER BY: {orderByClause}");
                }

                try
                {
                    var (successCount, skipCount, processedCount) = await MigrateBatchAsync(sourceTable, targetTable, offset, currentBatchSize, whereCondition, columnMappings, emptyToDashColumns, emptyValueReplacement, additionalColumns, additionalColumnsValues, orderByClause);
                    
                    totalSuccess += successCount;
                    totalSkipped += skipCount;
                    totalProcessed += processedCount;
                    
                    migratedRows += currentBatchSize;
                    _logger.LogInformation($"Batch {batchNumber} completed. Total migrated: {migratedRows}/{totalRows} (성공: {totalSuccess}, 중복건너뜀: {totalSkipped})");
                }
                catch (Exception ex)
                {
                    _logger.LogError($"========== BATCH ERROR ==========");
                    _logger.LogError($"Table: {sourceTable} -> {targetTable}");
                    _logger.LogError($"Batch {batchNumber} failed at offset {offset}, size {currentBatchSize}");
                    _logger.LogError($"Error: {ex.Message}");
                    _logger.LogError($"StackTrace: {ex.StackTrace}");
                    _logger.LogError($"=================================");
                    
                    // 로그 출력 완료를 보장
                    await Task.Delay(100);
                    Console.Out.Flush();
                    Console.Error.Flush();
                    
                    throw;
                }
            }

            _logger.LogInformation($"========================================");
            _logger.LogInformation($"[테이블 마이그레이션 완료: {sourceTable}]");
            _logger.LogInformation($"  전체 처리: {totalProcessed}");
            _logger.LogInformation($"  성공: {totalSuccess}");
            _logger.LogInformation($"  중복 건너뜀: {totalSkipped}");
            _logger.LogInformation($"========================================");
            
            return (totalSuccess, totalSkipped, totalProcessed);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to migrate table '{sourceTable}': {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Retrieves row count from SQL Server table.
    /// </summary>
    private async Task<long> GetRowCountAsync(string tableName, string? whereCondition = null)
    {
        using (var connection = new SqlConnection(_sqlServerConnectionString))
        {
            await connection.OpenAsync();
            string whereClause = string.IsNullOrWhiteSpace(whereCondition) ? string.Empty : $" WHERE {whereCondition}";
            string query = $"SELECT COUNT(*) FROM {tableName}{whereClause}";
            
            using (var command = new SqlCommand(query, connection))
            {
                command.CommandTimeout = _commandTimeout;
                var result = await command.ExecuteScalarAsync();
                return result != null ? Convert.ToInt64(result) : 0;
            }
        }
    }

    /// <summary>
    /// Migrates a batch of data from SQL Server to Oracle.
    /// Returns (successCount, skipCount, totalCount)
    /// </summary>
    private async Task<(int successCount, int skipCount, int totalCount)> MigrateBatchAsync(string sourceTable, string targetTable, int offset, int batchSize, string? whereCondition = null, Dictionary<string, string>? columnMappings = null, HashSet<string>? emptyToDashColumns = null, string? emptyValueReplacement = null, List<string>? additionalColumns = null, List<string>? additionalColumnsValues = null, string? orderByClause = null)
    {
        try
        {
            _logger.LogInformation($"[MigrateBatchAsync] 시작 - Table: {sourceTable}, Offset: {offset}, Size: {batchSize}");
            
            using (var sqlConnection = new SqlConnection(_sqlServerConnectionString))
            {
                await sqlConnection.OpenAsync();
                _logger.LogInformation($"[MigrateBatchAsync] SQL Server 연결 성공");

            // Read data from SQL Server with pagination
            // Use provided orderByClause or default to ORDER BY 1
            string actualOrderByClause = orderByClause ?? "ORDER BY 1";
            
            string whereClause = string.IsNullOrWhiteSpace(whereCondition) ? string.Empty : $" WHERE {whereCondition}";
            
            // REPEATABLEREAD 힌트 사용 - 가장 강력한 읽기 일관성 보장
            // REPEATABLEREAD: 트랜잭션 내에서 동일한 데이터를 반복 읽기해도 동일한 결과 보장
            // 페이지 분할, 중복 읽기, 팬텀 읽기(일부) 방지
            // READCOMMITTEDLOCK보다 더 강력하지만 성능 저하 있음
            string query = $@"
                SELECT * FROM {sourceTable} WITH (REPEATABLEREAD) {whereClause}
                {actualOrderByClause}
                OFFSET {offset} ROWS
                FETCH NEXT {batchSize} ROWS ONLY";

            _logger.LogInformation($"========================================");
            _logger.LogInformation($"[SQL QUERY] {query}");
            _logger.LogInformation($"========================================");
            
            // 연결 상태 확인
            _logger.LogInformation($"[MigrateBatchAsync] SQL Server 연결 상태: {sqlConnection.State}");
            Console.Out.Flush();
            
            // 연결이 열리지 않았다면 다시 열기
            if (sqlConnection.State != System.Data.ConnectionState.Open)
            {
                _logger.LogWarning($"[MigrateBatchAsync] 연결이 닫혀있음. 재연결 시도...");
                await sqlConnection.OpenAsync();
                _logger.LogInformation($"[MigrateBatchAsync] 재연결 성공");
            }
            Console.Out.Flush();

            using (var command = new SqlCommand(query, sqlConnection))
            {
                command.CommandTimeout = _commandTimeout;
                _logger.LogInformation($"[MigrateBatchAsync] SQL 명령 객체 생성 완료");
                _logger.LogInformation($"[MigrateBatchAsync] SQL 명령 실행 시작...");
                Console.Out.Flush();
                
                using (var reader = await command.ExecuteReaderAsync())
                {
                    _logger.LogInformation($"[MigrateBatchAsync] Reader 획득 완료. 데이터 로드 시작...");
                    _logger.LogInformation($"[MigrateBatchAsync] CommandTimeout: {command.CommandTimeout}초");
                    Console.Out.Flush();
                    
                    var dataTable = new DataTable();
                    var loadStartTime = DateTime.Now;
                    
                    try
                    {
                        dataTable.Load(reader);
                        var loadDuration = (DateTime.Now - loadStartTime).TotalSeconds;
                        
                        _logger.LogInformation($"[MigrateBatchAsync] SQL Server에서 {dataTable.Rows.Count}개 행 읽음 (소요시간: {loadDuration:F2}초)");
                        _logger.LogInformation($"[MigrateBatchAsync] DataTable 컬럼 수: {dataTable.Columns.Count}");
                        
                        // 중복 검증을 위한 첫 번째와 마지막 행의 PK 또는 첫 컬럼 값 로깅
                        if (dataTable.Rows.Count > 0)
                        {
                            var firstRow = dataTable.Rows[0];
                            var lastRow = dataTable.Rows[dataTable.Rows.Count - 1];
                            var firstColName = dataTable.Columns[0].ColumnName;
                            
                            _logger.LogInformation($"[배치 범위 검증] 첫 행 {firstColName}: {firstRow[0]}");
                            _logger.LogInformation($"[배치 범위 검증] 마지막 행 {firstColName}: {lastRow[0]}");
                            
                            // 배치 내 중복 검증: 첫 번째 컬럼 기준으로 중복 확인
                            var distinctCount = dataTable.AsEnumerable()
                                .Select(row => row[0]?.ToString() ?? "")
                                .Distinct()
                                .Count();
                            
                            if (distinctCount < dataTable.Rows.Count)
                            {
                                var duplicateCount = dataTable.Rows.Count - distinctCount;
                                _logger.LogWarning($"⚠️⚠️⚠️ [배치 내 중복 발견!] ⚠️⚠️⚠️");
                                _logger.LogWarning($"배치 내 전체 행: {dataTable.Rows.Count}");
                                _logger.LogWarning($"고유 {firstColName} 값: {distinctCount}");
                                _logger.LogWarning($"중복 행 수: {duplicateCount}");
                                _logger.LogWarning($"이는 SQL Server 쿼리에서 동일한 데이터를 여러 번 읽었음을 의미합니다!");
                                
                                // 중복된 값들을 찾아서 로깅
                                var duplicateValues = dataTable.AsEnumerable()
                                    .Select(row => row[0]?.ToString() ?? "")
                                    .GroupBy(x => x)
                                    .Where(g => g.Count() > 1)
                                    .Take(5) // 처음 5개만
                                    .Select(g => $"{g.Key} (x{g.Count()})")
                                    .ToList();
                                
                                if (duplicateValues.Any())
                                {
                                    _logger.LogWarning($"중복된 값 예시: {string.Join(", ", duplicateValues)}");
                                }
                            }
                            else
                            {
                                _logger.LogInformation($"✓ 배치 내 중복 없음 (고유 값: {distinctCount})");
                            }
                        }
                        
                        Console.Out.Flush();
                    }
                    catch (Exception loadEx)
                    {
                        _logger.LogError($"[MigrateBatchAsync] DataTable.Load 실패!");
                        _logger.LogError($"Error: {loadEx.Message}");
                        _logger.LogError($"StackTrace: {loadEx.StackTrace}");
                        Console.Out.Flush();
                        throw;
                    }

                    if (dataTable.Rows.Count == 0)
                    {
                        _logger.LogWarning($"[MigrateBatchAsync] 읽은 행이 0개. 반환.");
                        return (0, 0, 0);
                    }

                    // Insert into Oracle
                    _logger.LogInformation($"[MigrateBatchAsync] InsertIntoOracleAsync 호출 중...");
                    Console.Out.Flush();
                    
                    var (successCount, skipCount, totalCount) = await InsertIntoOracleAsync(targetTable, dataTable, columnMappings, emptyToDashColumns, emptyValueReplacement, additionalColumns, additionalColumnsValues);
                    
                    _logger.LogInformation($"[MigrateBatchAsync] InsertIntoOracleAsync 완료 (성공: {successCount}, 건너뜀: {skipCount})");
                    Console.Out.Flush();
                    
                    return (successCount, skipCount, totalCount);
                }
            }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"========== MigrateBatchAsync 실패 ==========");
            _logger.LogError($"Table: {sourceTable}, Offset: {offset}, Size: {batchSize}");
            _logger.LogError($"Error Type: {ex.GetType().Name}");
            _logger.LogError($"Error Message: {ex.Message}");
            _logger.LogError($"StackTrace: {ex.StackTrace}");
            if (ex.InnerException != null)
            {
                _logger.LogError($"InnerException: {ex.InnerException.Message}");
            }
            _logger.LogError($"==========================================");
            
            // 로그 출력 완료 보장
            await Task.Delay(200);
            Console.Out.Flush();
            Console.Error.Flush();
            
            throw;
        }
    }

    /// <summary>
    /// Inserts data into Oracle table.
    /// Maps SQL Server data types to Oracle equivalents.
    /// Supports column mapping if provided (SQL Server column name -> Oracle column name).
    /// Returns (successCount, skipCount, totalCount)
    /// </summary>
    private async Task<(int successCount, int skipCount, int totalCount)> InsertIntoOracleAsync(string tableName, DataTable dataTable, Dictionary<string, string>? columnMappings = null, HashSet<string>? emptyToDashColumns = null, string? emptyValueReplacement = null, List<string>? additionalColumns = null, List<string>? additionalColumnsValues = null)
    {
        _logger.LogInformation($"[InsertIntoOracleAsync] 시작 - Table: {tableName}, Rows: {dataTable.Rows.Count}");
        
        using (var oracleConnection = new OracleConnection(_oracleConnectionString))
        {
            _logger.LogInformation($"[InsertIntoOracleAsync] Oracle 연결 중...");
            await oracleConnection.OpenAsync();
            _logger.LogInformation($"[InsertIntoOracleAsync] Oracle 연결 성공");

            using (var transaction = oracleConnection.BeginTransaction())
            {
                _logger.LogInformation($"[InsertIntoOracleAsync] 트랜잭션 시작");
                int successCount = 0;
                int skipCount = 0;
                
                try
                {
                    int rowIndex = 0;
                    foreach (DataRow row in dataTable.Rows)
                    {
                        rowIndex++;
                        if (rowIndex % 100 == 0)
                        {
                            _logger.LogInformation($"[InsertIntoOracleAsync] 진행 중: {rowIndex}/{dataTable.Rows.Count} 행 (성공: {successCount}, 중복건너뜀: {skipCount})");
                        }

                        // 컬럼명 유효성 검사 및 Oracle 식별자 쌍따옴표 처리
                        var validColumns = dataTable.Columns.Cast<DataColumn>()
                            .Where(c => !string.IsNullOrWhiteSpace(c.ColumnName) && c.ColumnName.All(ch => char.IsLetterOrDigit(ch) || ch == '_'))
                            .ToList();

                        if (validColumns.Count == 0)
                        {
                            _logger.LogWarning($"[{tableName}] 컬럼명이 비어있거나 유효하지 않아 INSERT를 건너뜁니다.");
                            continue;
                        }

                        // G/H열 매핑이 지정된 경우: columnMappings의 키(G열)에 해당하는 컬럼만 선택
                        List<DataColumn> columnsToInsert = validColumns;
                        if (columnMappings != null && columnMappings.Count > 0)
                        {
                            var mappedColumnNames = new HashSet<string>(columnMappings.Keys, StringComparer.OrdinalIgnoreCase);
                            columnsToInsert = validColumns.Where(c => mappedColumnNames.Contains(c.ColumnName)).ToList();
                            
                            if (columnsToInsert.Count == 0)
                            {
                                _logger.LogWarning($"[{tableName}] G열 매핑에 해당하는 컬럼을 찾을 수 없습니다. 건너뜁니다.");
                                continue;
                            }
                        }

                        // 컬럼 매핑 적용: SQL Server 컬럼명 -> Oracle 컬럼명
                        var mappedColumns = columnsToInsert.Select(c =>
                        {
                            var oracleColName = columnMappings?.ContainsKey(c.ColumnName) == true 
                                ? columnMappings[c.ColumnName] 
                                : c.ColumnName;
                            return new { Source = c.ColumnName, Target = oracleColName };
                        }).ToList();

                        // 추가 컬럼(K열) 준비
                        var allTargetColumns = new List<string>(mappedColumns.Select(c => $"\"{c.Target}\""));
                        var valueExpressions = new List<string>(mappedColumns.Select((c, i) => $":p{i}"));

                        // 추가 컬럼(L열) 값 또는 식 처리
                        if (additionalColumns != null && additionalColumns.Count > 0)
                        {
                            for (int ai = 0; ai < additionalColumns.Count; ai++)
                            {
                                allTargetColumns.Add($"\"{additionalColumns[ai]}\"");
                                
                                if (ai < (additionalColumnsValues?.Count ?? 0))
                                {
                                    var rawExpr = additionalColumnsValues![ai];
                                    var builtExpr = BuildAdditionalExpressionForInsert(rawExpr, mappedColumns.Cast<object>().ToList(), validColumns, row);
                                    valueExpressions.Add(builtExpr);
                                }
                                else
                                {
                                    valueExpressions.Add("NULL");
                                }
                            }
                        }

                        var columnNames = string.Join(", ", allTargetColumns);
                        var parameterNames = string.Join(", ", valueExpressions);

                        if (string.IsNullOrWhiteSpace(columnNames) || string.IsNullOrWhiteSpace(parameterNames))
                        {
                            _logger.LogWarning($"[{tableName}] INSERT 구문 생성 실패: 컬럼 또는 파라미터가 비어있음");
                            continue;
                        }

                        string insertQuery = $"INSERT INTO {tableName} ({columnNames}) VALUES ({parameterNames})";

                        using (var command = new OracleCommand(insertQuery, oracleConnection))
                        {
                            command.Transaction = transaction;
                            command.CommandTimeout = _commandTimeout;

                            // Add parameters with type mapping (columnsToInsert만 사용)
                            for (int i = 0; i < columnsToInsert.Count; i++)
                            {
                                var sourceColName = columnsToInsert[i].ColumnName;
                                var colIdx = dataTable.Columns.IndexOf(sourceColName);
                                var value = row[colIdx] == DBNull.Value ? null : row[colIdx];

                                // If this source column is configured to convert empty/whitespace to a replacement, apply it
                                if (emptyToDashColumns != null && emptyToDashColumns.Count > 0 && emptyToDashColumns.Contains(sourceColName))
                                {
                                    if (value is string s && string.IsNullOrWhiteSpace(s))
                                    {
                                        value = string.IsNullOrEmpty(emptyValueReplacement) ? "-" : emptyValueReplacement;
                                    }
                                }

                                command.Parameters.Add($":p{i}", value ?? DBNull.Value);
                            }

                            try
                            {
                                await command.ExecuteNonQueryAsync();
                                successCount++;
                            }
                            catch (OracleException oracleEx) when (oracleEx.Number == 1) // ORA-00001: unique constraint violated
                            {
                                // 중복 키 오류 - 해당 행만 건너뛰고 계속 진행
                                skipCount++;
                                
                                // 중복 데이터를 로그 파일에 기록
                                LogDuplicateRow(tableName, row, dataTable);
                                
                                // 처음 몇 건은 상세 정보 로깅
                                if (skipCount <= 5)
                                {
                                    _logger.LogWarning($"[{tableName}] 중복 키 #{skipCount}: {oracleEx.Message}");
                                    // 첫 3개 컬럼 값 출력
                                    var keyInfo = new List<string>();
                                    for (int ci = 0; ci < Math.Min(3, dataTable.Columns.Count); ci++)
                                    {
                                        var colName = dataTable.Columns[ci].ColumnName;
                                        var colValue = row[ci];
                                        keyInfo.Add($"{colName}={colValue}");
                                    }
                                    _logger.LogWarning($"  SQL Server 키 정보: {string.Join(", ", keyInfo)}");
                                    
                                    // INSDTTM 값 상세 분석 (밀리초/나노초 확인)
                                    if (dataTable.Columns.Contains("INSDTTM") && row["INSDTTM"] != DBNull.Value)
                                    {
                                        var insdttm = row["INSDTTM"];
                                        if (insdttm is DateTime dt)
                                        {
                                            _logger.LogWarning($"  INSDTTM 상세: {dt:yyyy-MM-dd HH:mm:ss.fffffff} (밀리초까지)");
                                            var toCharSimulation = dt.ToString("yyyyMMddHHmmssffffff") + "00";
                                            _logger.LogWarning($"  TO_CHAR 시뮬레이션 (YYYYMMDDHHMMSSF9): {toCharSimulation}");
                                            _logger.LogWarning($"  ⚠️ 주의: F9 포맷은 9자리지만 DATETIME은 7자리까지만 지원!");
                                            _logger.LogWarning($"  ⚠️ 다른 INSDTTM 값도 동일한 TO_CHAR 결과를 생성할 수 있습니다!");
                                        }
                                    }
                                }
                                else if (skipCount == 1 || skipCount % 100 == 0)
                                {
                                    _logger.LogWarning($"[{tableName}] 중복 키로 인해 행 건너뜀 (누적: {skipCount}개) - 로그 파일에 기록됨");
                                }
                                // 트랜잭션은 유지하고 다음 행으로 계속
                                continue;
                            }
                            catch (OracleException oracleEx)
                            {
                                _logger.LogError($"========== ORACLE EXECUTE ERROR ==========");
                                _logger.LogError($"Table: {tableName}");
                                _logger.LogError($"Query: {insertQuery}");
                                _logger.LogError($"Oracle Error Number: {oracleEx.Number}");
                                _logger.LogError($"Oracle Error Message: {oracleEx.Message}");
                                _logger.LogError($"StackTrace: {oracleEx.StackTrace}");
                                _logger.LogError($"==========================================");
                                
                                // 로그 출력 완료를 보장
                                await Task.Delay(100);
                                Console.Out.Flush();
                                Console.Error.Flush();
                                
                                throw;
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError($"========== EXECUTE ERROR ==========");
                                _logger.LogError($"Table: {tableName}");
                                _logger.LogError($"Query: {insertQuery}");
                                _logger.LogError($"Error: {ex.GetType().Name} - {ex.Message}");
                                _logger.LogError($"StackTrace: {ex.StackTrace}");
                                if (ex.InnerException != null)
                                {
                                    _logger.LogError($"InnerException: {ex.InnerException.Message}");
                                }
                                _logger.LogError($"===================================");
                                
                                // 로그 출력 완료를 보장
                                await Task.Delay(100);
                                Console.Out.Flush();
                                Console.Error.Flush();
                                
                                throw;
                            }
                        }
                    }

                    _logger.LogInformation($"========================================");
                    _logger.LogInformation($"[배치 처리 완료]");
                    _logger.LogInformation($"  전체 행: {dataTable.Rows.Count}");
                    _logger.LogInformation($"  성공: {successCount}");
                    _logger.LogInformation($"  중복 건너뜀: {skipCount}");
                    _logger.LogInformation($"========================================");
                    
                    await transaction.CommitAsync();
                    _logger.LogInformation($"[InsertIntoOracleAsync] 커밋 성공");
                    
                    if (skipCount > 0)
                    {
                        _logger.LogWarning($"⚠ 주의: {skipCount}개 행이 중복으로 인해 건너뛰어졌습니다.");
                    }
                    
                    return (successCount, skipCount, dataTable.Rows.Count);
                }
                catch (Exception ex)
                {
                    _logger.LogError($"[InsertIntoOracleAsync] 트랜잭션 오류 발생. 롤백 수행 중...");
                    await transaction.RollbackAsync();
                    _logger.LogError($"Transaction rolled back due to: {ex.Message}");
                    
                    // 로그 플러시
                    await Task.Delay(100);
                    Console.Out.Flush();
                    Console.Error.Flush();
                    
                    throw;
                }
            }
        }
    }

    /// <summary>
    /// Gets primary key columns for a table from SQL Server.
    /// </summary>
    private async Task<List<string>> GetPrimaryKeyColumnsAsync(string tableName)
    {
        var pkColumns = new List<string>();

        using (var connection = new SqlConnection(_sqlServerConnectionString))
        {
            await connection.OpenAsync();

            // Extract schema and table name
            string schemaName = "dbo";
            string tableNameOnly = tableName;
            if (tableName.Contains('.'))
            {
                var parts = tableName.Split('.');
                schemaName = parts[0];
                tableNameOnly = parts[1];
            }

            _logger.LogInformation($"[GetPrimaryKeyColumns] 시작 - Table: {tableName}, Schema: {schemaName}, TableOnly: {tableNameOnly}");

            // 방법 1: INFORMATION_SCHEMA 사용 (가장 호환성 높음)
            string query1 = @"
                SELECT c.COLUMN_NAME, c.ORDINAL_POSITION
                FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
                JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE c 
                    ON tc.CONSTRAINT_NAME = c.CONSTRAINT_NAME
                    AND tc.TABLE_SCHEMA = c.TABLE_SCHEMA
                    AND tc.TABLE_NAME = c.TABLE_NAME
                WHERE tc.CONSTRAINT_TYPE = 'PRIMARY KEY'
                    AND tc.TABLE_SCHEMA = @schemaName
                    AND tc.TABLE_NAME = @tableName
                ORDER BY c.ORDINAL_POSITION";

            using (var command = new SqlCommand(query1, connection))
            {
                command.CommandTimeout = _commandTimeout;
                command.Parameters.AddWithValue("@schemaName", schemaName);
                command.Parameters.AddWithValue("@tableName", tableNameOnly);
                
                _logger.LogInformation($"[GetPrimaryKeyColumns] INFORMATION_SCHEMA 쿼리 실행 중...");
                
                using (var reader = await command.ExecuteReaderAsync())
                {
                    int position = 1;
                    while (await reader.ReadAsync())
                    {
                        var columnName = reader.GetString(0);
                        var ordinal = reader.GetInt32(1);
                        pkColumns.Add(columnName);
                        _logger.LogInformation($"[GetPrimaryKeyColumns] {tableName} - PK #{position} (ORDINAL: {ordinal}): {columnName}");
                        position++;
                    }
                }
            }
            
            // 방법 2: sys.indexes 사용 (방법 1 실패 시)
            if (pkColumns.Count == 0)
            {
                _logger.LogWarning($"[GetPrimaryKeyColumns] INFORMATION_SCHEMA에서 PK를 찾지 못함. sys.indexes 시도...");
                
                string query2 = @"
                    SELECT col.name AS COLUMN_NAME, ic.key_ordinal
                    FROM sys.indexes idx
                    INNER JOIN sys.index_columns ic 
                        ON idx.object_id = ic.object_id AND idx.index_id = ic.index_id
                    INNER JOIN sys.columns col 
                        ON ic.object_id = col.object_id AND ic.column_id = col.column_id
                    INNER JOIN sys.tables t 
                        ON idx.object_id = t.object_id
                    INNER JOIN sys.schemas s 
                        ON t.schema_id = s.schema_id
                    WHERE idx.is_primary_key = 1
                        AND s.name = @schemaName
                        AND t.name = @tableName
                    ORDER BY ic.key_ordinal";

                using (var command = new SqlCommand(query2, connection))
                {
                    command.CommandTimeout = _commandTimeout;
                    command.Parameters.AddWithValue("@schemaName", schemaName);
                    command.Parameters.AddWithValue("@tableName", tableNameOnly);
                    
                    using (var reader = await command.ExecuteReaderAsync())
                    {
                        int position = 1;
                        while (await reader.ReadAsync())
                        {
                            var columnName = reader.GetString(0);
                            var keyOrdinal = reader.GetInt32(1);
                            pkColumns.Add(columnName);
                            _logger.LogInformation($"[GetPrimaryKeyColumns] {tableName} - PK #{position} (KEY_ORDINAL: {keyOrdinal}): {columnName}");
                            position++;
                        }
                    }
                }
            }
        }

        if (pkColumns.Count > 0)
        {
            _logger.LogInformation($"[GetPrimaryKeyColumns] {tableName} - 총 {pkColumns.Count}개 PK 컬럼 조회 완료: {string.Join(", ", pkColumns)}");
        }
        else
        {
            _logger.LogWarning($"[GetPrimaryKeyColumns] {tableName} - Primary Key를 찾을 수 없습니다!");
        }

        return pkColumns;
    }

    /// <summary>
    /// Gets all column names for a table from SQL Server (fallback when no PK exists).
    /// </summary>
    private async Task<List<string>> GetAllColumnsAsync(string tableName)
    {
        var columns = new List<string>();

        using (var connection = new SqlConnection(_sqlServerConnectionString))
        {
            await connection.OpenAsync();

            // Extract schema and table name
            string schemaName = "dbo";
            string tableNameOnly = tableName;
            if (tableName.Contains('.'))
            {
                var parts = tableName.Split('.');
                schemaName = parts[0];
                tableNameOnly = parts[1];
            }

            string query = @"
                SELECT COLUMN_NAME
                FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_SCHEMA = @schemaName
                    AND TABLE_NAME = @tableName
                ORDER BY ORDINAL_POSITION";

            using (var command = new SqlCommand(query, connection))
            {
                command.CommandTimeout = _commandTimeout;
                command.Parameters.AddWithValue("@schemaName", schemaName);
                command.Parameters.AddWithValue("@tableName", tableNameOnly);
                
                using (var reader = await command.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        columns.Add(reader.GetString(0));
                    }
                }
            }
        }

        return columns;
    }

    /// <summary>
    /// Gets list of tables from SQL Server.
    /// </summary>
    public async Task<List<string>> GetSourceTablesAsync()
    {
        var tables = new List<string>();

        using (var connection = new SqlConnection(_sqlServerConnectionString))
        {
            await connection.OpenAsync();

            string query = @"
                SELECT TABLE_NAME 
                FROM INFORMATION_SCHEMA.TABLES 
                WHERE TABLE_TYPE = 'BASE TABLE'
                ORDER BY TABLE_NAME";

            using (var command = new SqlCommand(query, connection))
            {
                command.CommandTimeout = _commandTimeout;
                using (var reader = await command.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        tables.Add(reader.GetString(0));
                    }
                }
            }
        }

        return tables;
    }

    /// <summary>
    /// Deletes all rows from an Oracle table (useful for re-running migrations).
    /// </summary>
    public async Task DeleteOracleTableAsync(string tableName)
    {
        // 변경: TRUNCATE 대신 DELETE FROM을 사용하여 데이터를 삭제하도록 합니다.
        // 이유: 일부 환경에서 TRUNCATE 권한이 없거나, 트랜잭션 관리를 명시적으로 하기 위함입니다.
        using (var connection = new OracleConnection(_oracleConnectionString))
        {
            await connection.OpenAsync();

            using (var transaction = connection.BeginTransaction())
            {
                try
                {
                    using (var command = new OracleCommand($"DELETE FROM {tableName}", connection))
                    {
                        command.Transaction = transaction;
                        command.CommandTimeout = _commandTimeout;
                        int affected = await command.ExecuteNonQueryAsync();
                        _logger.LogInformation($"Deleted {affected} rows from '{tableName}' in Oracle");
                    }

                    await transaction.CommitAsync();
                }
                catch (Exception ex)
                {
                    try
                    {
                        await transaction.RollbackAsync();
                    }
                    catch { }

                    _logger.LogError($"Failed to delete rows from '{tableName}': {ex.Message}");
                    throw;
                }
            }
        }
    }

    /// <summary>
    /// Excel 매핑 파일을 기반으로 테이블을 마이그레이션합니다.
    /// </summary>
    public async Task MigrateWithMappingAsync(List<TableMapping> mappings)
    {
        try
        {
            _logger.LogInformation($"Excel 매핑을 기반으로 마이그레이션 시작합니다.");
            _logger.LogInformation($"========================================");

            // 활성화된 매핑만 필터링
            var activeMappings = mappings.Where(m => m.IsActive).ToList();
            _logger.LogInformation($"총 {mappings.Count}개 중 {activeMappings.Count}개의 활성 매핑을 처리합니다.");

            if (activeMappings.Count == 0)
            {
                _logger.LogWarning("활성화된 매핑이 없습니다.");
                return;
            }

            int successCount = 0;
            int failureCount = 0;

            foreach (var mapping in activeMappings)
            {
                try
                {
                    // 시작 시간 기록
                    mapping.StartTime = DateTime.Now;
                    mapping.Status = "진행 중";
                    
                    _logger.LogInformation($"마이그레이션 시작: {mapping.SqlServerTableName} -> {mapping.OracleTableName}");
                    if (!string.IsNullOrEmpty(mapping.Description))
                    {
                        _logger.LogInformation($"  설명: {mapping.Description}");
                    }

                    // 대상 테이블 초기화 플래그 확인
                    _logger.LogInformation($"  Excel F열 (DeleteTarget): {mapping.DeleteTarget}");
                    
                    if (mapping.DeleteTarget)
                    {
                        try
                        {
                            _logger.LogInformation($"  ★★★ 대상 테이블 데이터 삭제 시작: {mapping.OracleTableName} ★★★");
                            await DeleteOracleTableAsync(mapping.OracleTableName);
                            _logger.LogInformation($"  ★★★ 삭제 완료 ★★★");
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError($"  ✗✗✗ 대상 테이블 초기화 실패: {ex.Message} ✗✗✗");
                            _logger.LogError($"  이미 데이터가 있어 중복 키 오류가 발생할 수 있습니다!");
                            throw; // 삭제 실패 시 중단
                        }
                    }
                    else
                    {
                        _logger.LogWarning($"  ⚠ DeleteTarget이 FALSE입니다. Oracle 테이블에 이미 데이터가 있으면 중복 키 오류가 발생합니다!");
                    }

                    var (insertedCount, skippedCount, totalProcessed) = await MigrateTableAsync(mapping.SqlServerTableName, mapping.OracleTableName, mapping.WhereCondition, mapping.ColumnMappings, mapping.EmptyToDashColumns, mapping.EmptyValueReplacement, mapping.AdditionalColumns, mapping.AdditionalColumnsValues, mapping.OrderByColumns);
                    
                    // 완료 시간 및 통계 기록
                    mapping.EndTime = DateTime.Now;
                    mapping.RecordCount = insertedCount;
                    mapping.SkippedCount = skippedCount;
                    mapping.TotalProcessed = totalProcessed;
                    mapping.Status = "완료";
                    successCount++;
                    
                    var duration = mapping.EndTime.Value - mapping.StartTime.Value;
                    _logger.LogInformation($"✓ {mapping.SqlServerTableName} 마이그레이션 완료 (소요 시간: {duration.TotalSeconds:F2}초)");
                }
                catch (Exception ex)
                {
                    // 실패 시간 및 오류 메시지 기록
                    mapping.EndTime = DateTime.Now;
                    mapping.Status = "실패";
                    mapping.ErrorMessage = ex.Message;
                    failureCount++;
                    _logger.LogError($"========== TABLE MIGRATION FAILED ==========");
                    _logger.LogError($"✗ {mapping.SqlServerTableName} -> {mapping.OracleTableName}");
                    _logger.LogError($"Error: {ex.Message}");
                    _logger.LogError($"InnerException: {ex.InnerException?.Message}");
                    _logger.LogError($"StackTrace: {ex.StackTrace}");
                    _logger.LogError($"===========================================");
                }
            }

            _logger.LogInformation($"========================================");
            _logger.LogInformation($"마이그레이션 완료: 성공 {successCount}개, 실패 {failureCount}개");
        }
        catch (Exception ex)
        {
            _logger.LogError($"Excel 매핑 기반 마이그레이션 중 치명적 오류 발생: {ex.Message}");
            _logger.LogError($"스택 트레이스: {ex.StackTrace}");
            // throw 제거: Excel 업데이트가 항상 실행되도록 보장
        }
    }

    /// <summary>
    /// Excel 매핑 정보를 기반으로 테이블들을 마이그레이션합니다.
    /// </summary>
    public async Task MigrateTablesFromMappingAsync(List<TableMapping> mappings, bool truncateFirst = false)
    {
        if (!mappings.Any())
        {
            _logger.LogWarning("마이그레이션할 매핑 정보가 없습니다.");
            return;
        }

        var activeMappings = mappings.Where(m => m.IsActive).ToList();
        _logger.LogInformation($"========================================");
        _logger.LogInformation($"마이그레이션 시작: {activeMappings.Count}개 테이블");
        _logger.LogInformation($"========================================");

        int successCount = 0;
        int failureCount = 0;

        foreach (var mapping in activeMappings)
        {
            try
            {
                _logger.LogInformation($"");
                _logger.LogInformation($"[{successCount + failureCount + 1}/{activeMappings.Count}] 마이그레이션: {mapping.SqlServerTableName} -> {mapping.OracleTableName}");
                if (!string.IsNullOrEmpty(mapping.Description))
                {
                    _logger.LogInformation($"  설명: {mapping.Description}");
                }

                // 선택적으로 대상 테이블 초기화 (전역 옵션 또는 매핑별 옵션)
                if (truncateFirst || mapping.DeleteTarget)
                {
                    try
                    {
                        _logger.LogInformation($"  대상 테이블 초기화: {mapping.OracleTableName}");
                        await DeleteOracleTableAsync(mapping.OracleTableName);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning($"테이블 초기화 실패: {ex.Message}. 계속 진행합니다.");
                    }
                }

                // 테이블 마이그레이션 실행
                await MigrateTableAsync(mapping.SqlServerTableName, mapping.OracleTableName, mapping.WhereCondition, mapping.ColumnMappings, mapping.EmptyToDashColumns, mapping.EmptyValueReplacement, mapping.AdditionalColumns, mapping.AdditionalColumnsValues);
                successCount++;
                _logger.LogInformation($"  ✓ 완료");
            }
            catch (Exception ex)
            {
                failureCount++;
                _logger.LogError($"  ✗ 실패: {ex.Message}");
                _logger.LogError($"  Stack Trace: {ex.StackTrace}");
            }
        }

        _logger.LogInformation($"");
        _logger.LogInformation($"========================================");
        _logger.LogInformation($"마이그레이션 완료");
        _logger.LogInformation($"  성공: {successCount}개 테이블");
        _logger.LogInformation($"  실패: {failureCount}개 테이블");
        _logger.LogInformation($"========================================");
    }

    /// <summary>
    /// DB 연결 없이 로컬에서 INSERT 구문 생성을 미리보기합니다.
    /// 추가 컬럼(L열)의 식에서 {Col} 토큰을 :pN 파라미터로 치환하여 출력합니다.
    /// 이 메서드는 Oracle/SQL Server에 연결하지 않으며 단순히 로그로 결과를 보여줍니다.
    /// </summary>
    public Task PreviewInsertsLocalAsync(List<TableMapping> mappings, int sampleRows = 3)
    {
        _logger.LogInformation("로컬 미리보기(데이터베이스 연결 없음)를 시작합니다.");

        foreach (var mapping in mappings.Where(m => m.IsActive))
        {
            _logger.LogInformation($"--- 매핑: {mapping.SqlServerTableName} -> {mapping.OracleTableName} ---");

            // 결정된 소스 컬럼 목록: 매핑된 컬럼들 또는 L열에서 참조되는 {Col} 토큰
            var sourceCols = new List<string>();
            if (mapping.ColumnMappings != null && mapping.ColumnMappings.Count > 0)
            {
                sourceCols.AddRange(mapping.ColumnMappings.Keys);
            }

            // L열 내에서 {ColName} 형식으로 참조되는 컬럼들을 추가
            foreach (var raw in mapping.AdditionalColumnsValues ?? Enumerable.Empty<string>())
            {
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(raw, "\u007B([^}]+)\u007D"))
                {
                    var col = m.Groups[1].Value.Trim();
                    if (!string.IsNullOrWhiteSpace(col) && !sourceCols.Any(s => string.Equals(s, col, StringComparison.OrdinalIgnoreCase)))
                        sourceCols.Add(col);
                }
            }

            if (sourceCols.Count == 0)
            {
                // 최소한 한 개의 가상 컬럼을 생성하여 파라미터 치환을 보여줍니다.
                sourceCols.Add("SampleCol");
            }

            // 샘플 데이터 로우 생성
            var table = new DataTable();
            foreach (var c in sourceCols)
                table.Columns.Add(c, typeof(string));

            for (int r = 0; r < sampleRows; r++)
            {
                var row = table.NewRow();
                foreach (DataColumn col in table.Columns)
                {
                    // 날짜/시간 컬럼명인 경우 현재 시각을 삽입
                    if (col.ColumnName.IndexOf("DTTM", StringComparison.OrdinalIgnoreCase) >= 0 || col.ColumnName.IndexOf("DATE", StringComparison.OrdinalIgnoreCase) >= 0 || col.ColumnName.IndexOf("TIME", StringComparison.OrdinalIgnoreCase) >= 0)
                        row[col.ColumnName] = DateTime.Now.ToString("o");
                    else
                        row[col.ColumnName] = $"sample_{col.ColumnName}_{r + 1}";
                }
                table.Rows.Add(row);
            }

            // 매핑된 대상 컬럼명 결정
            var mappedColumns = new List<(string Source, string Target)>();
            foreach (DataColumn dc in table.Columns)
            {
                var src = dc.ColumnName;
                var tgt = (mapping.ColumnMappings != null && mapping.ColumnMappings.TryGetValue(src, out var t)) ? t : src;
                mappedColumns.Add((src, tgt));
            }

            // 추가 컬럼(Oracle 전용) 처리
            var additionalCols = mapping.AdditionalColumns ?? new List<string>();
            var additionalVals = mapping.AdditionalColumnsValues ?? new List<string>();

            // INSERT 컬럼 목록
            var allTargetCols = new List<string>(mappedColumns.Select(m => m.Target));
            allTargetCols.AddRange(additionalCols);

            // 각 샘플 로우에 대해 생성되는 INSERT를 출력
            for (int r = 0; r < table.Rows.Count; r++)
            {
                var row = table.Rows[r];

                // 파라미터 매핑: 소스 컬럼 순서대로 :p0, :p1, ...
                var paramMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < mappedColumns.Count; i++)
                {
                    paramMap[mappedColumns[i].Source] = $":p{i}";
                }

                // 대상 값 표현식 생성: 매핑된 컬럼들은 파라미터로, 추가 컬럼들은 식 또는 리터럴
                var valueExpressions = new List<string>();
                for (int i = 0; i < mappedColumns.Count; i++)
                {
                    valueExpressions.Add(paramMap[mappedColumns[i].Source]);
                }

                // 추가 식 치환
                for (int ai = 0; ai < additionalCols.Count; ai++)
                {
                    string expr = ai < additionalVals.Count ? additionalVals[ai] : "NULL";
                    var built = BuildAdditionalExpressionLocal(expr, paramMap);
                    valueExpressions.Add(built);
                }

                var colList = string.Join(", ", allTargetCols.Select(c => $"\"{c}\""));
                var valList = string.Join(", ", valueExpressions);

                var insert = $"INSERT INTO {mapping.OracleTableName} ({colList}) VALUES ({valList});";
                _logger.LogInformation($"[Preview row {r + 1}] {insert}");

                // 파라미터 값 로그
                for (int i = 0; i < mappedColumns.Count; i++)
                {
                    var src = mappedColumns[i].Source;
                    var paramName = $":p{i}";
                    var val = row[src];
                    _logger.LogInformation($"  {paramName} -> {src} = {val}");
                }
            }
        }

        _logger.LogInformation("로컬 미리보기 종료");
        return Task.CompletedTask;
    }

    private string BuildAdditionalExpressionLocal(string rawExpr, Dictionary<string, string> sourceParamMap)
    {
        if (string.IsNullOrWhiteSpace(rawExpr))
            return "NULL";

        // 간단한 안전 검사
        var lower = rawExpr.ToLowerInvariant();
        if (lower.Contains(";") || lower.Contains("--") || lower.Contains("/*") || lower.Contains("*/"))
            throw new InvalidOperationException("추가 식에 허용되지 않는 문자가 포함되어 있습니다.");

        // {Col} 토큰을 파라미터로 치환
        var result = rawExpr;
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(rawExpr, "\u007B([^}]+)\u007D"))
        {
            var col = m.Groups[1].Value.Trim();
            if (sourceParamMap.TryGetValue(col, out var pname))
            {
                result = result.Replace(m.Value, pname);
            }
            else
            {
                // 대소문자 차이 허용
                var found = sourceParamMap.Keys.FirstOrDefault(k => string.Equals(k, col, StringComparison.OrdinalIgnoreCase));
                if (found != null)
                {
                    result = result.Replace(m.Value, sourceParamMap[found]);
                }
                else
                {
                    // 없는 컬럼 참조는 NULL로 치환
                    result = result.Replace(m.Value, "NULL");
                }
            }
        }

        return result;
    }

    /// <summary>
    /// INSERT 실행 시 추가 컬럼(L열) 식을 평가합니다.
    /// {Col} 토큰을 :pN 파라미터로 또는 실제 값으로 치환합니다.
    /// </summary>
    private string BuildAdditionalExpressionForInsert(string rawExpr, List<object> mappedColumns, List<DataColumn> validColumns, DataRow row)
    {
        if (string.IsNullOrWhiteSpace(rawExpr))
            return "NULL";

        // 간단한 안전 검사
        var lower = rawExpr.ToLowerInvariant();
        if (lower.Contains(";") || lower.Contains("--") || lower.Contains("/*") || lower.Contains("*/"))
            throw new InvalidOperationException("추가 식에 허용되지 않는 문자가 포함되어 있습니다.");

        // {Col} 토큰 맵 생성: {Col} → :pN 파라미터
        var paramMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < mappedColumns.Count; i++)
        {
            dynamic mc = mappedColumns[i];
            paramMap[mc.Source] = $":p{i}";
        }

        // {Col} 토큰을 파라미터로 치환
        var result = rawExpr;
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(rawExpr, "\u007B([^}]+)\u007D"))
        {
            var col = m.Groups[1].Value.Trim();
            if (paramMap.TryGetValue(col, out var pname))
            {
                result = result.Replace(m.Value, pname);
            }
            else
            {
                // 없는 컬럼 참조는 NULL로 치환
                result = result.Replace(m.Value, "NULL");
            }
        }

        return result;
    }
}

