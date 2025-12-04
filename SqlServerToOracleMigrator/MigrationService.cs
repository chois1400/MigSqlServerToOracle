using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Oracle.ManagedDataAccess.Client;
using Dapper;
using System.Data;

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
    }

    /// <summary>
    /// Migrates a specific table from SQL Server to Oracle.
    /// If a whereCondition is provided, it will be used in the SELECT statement to filter rows.
    /// If columnMappings is provided, column names will be mapped during INSERT.
    /// </summary>
    public async Task MigrateTableAsync(string sourceTable, string targetTable, string? whereCondition = null, Dictionary<string, string>? columnMappings = null, HashSet<string>? emptyToDashColumns = null, string? emptyValueReplacement = null, List<string>? additionalColumns = null, List<string>? additionalColumnsValues = null)
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
                return;
            }

            // Migrate in batches
            long migratedRows = 0;
            int batchNumber = 0;

            while (migratedRows < totalRows)
            {
                batchNumber++;
                int offset = (int)migratedRows;
                int currentBatchSize = Math.Min(_batchSize, (int)(totalRows - migratedRows));

                _logger.LogInformation($"Processing batch {batchNumber}: offset={offset}, size={currentBatchSize}");

                try
                {
                    await MigrateBatchAsync(sourceTable, targetTable, offset, currentBatchSize, whereCondition, columnMappings, emptyToDashColumns, emptyValueReplacement, additionalColumns, additionalColumnsValues);
                    migratedRows += currentBatchSize;
                    _logger.LogInformation($"Batch {batchNumber} completed. Total migrated: {migratedRows}/{totalRows}");
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Error in batch {batchNumber}: {ex.Message}");
                    throw;
                }
            }

            _logger.LogInformation($"Successfully migrated {migratedRows} rows from '{sourceTable}' to '{targetTable}'");
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
    /// </summary>
    private async Task MigrateBatchAsync(string sourceTable, string targetTable, int offset, int batchSize, string? whereCondition = null, Dictionary<string, string>? columnMappings = null, HashSet<string>? emptyToDashColumns = null, string? emptyValueReplacement = null, List<string>? additionalColumns = null, List<string>? additionalColumnsValues = null)
    {
        using (var sqlConnection = new SqlConnection(_sqlServerConnectionString))
        {
            await sqlConnection.OpenAsync();

            // Read data from SQL Server with pagination
            string whereClause = string.IsNullOrWhiteSpace(whereCondition) ? string.Empty : $" WHERE {whereCondition}";
            string query = $@"
                SELECT * FROM {sourceTable}{whereClause}
                ORDER BY (SELECT NULL)
                OFFSET {offset} ROWS
                FETCH NEXT {batchSize} ROWS ONLY";

            using (var command = new SqlCommand(query, sqlConnection))
            {
                command.CommandTimeout = _commandTimeout;
                using (var reader = await command.ExecuteReaderAsync())
                {
                    var dataTable = new DataTable();
                    dataTable.Load(reader);

                    if (dataTable.Rows.Count == 0)
                        return;

                    // Insert into Oracle
                    await InsertIntoOracleAsync(targetTable, dataTable, columnMappings, emptyToDashColumns, emptyValueReplacement, additionalColumns, additionalColumnsValues);
                }
            }
        }
    }

    /// <summary>
    /// Inserts data into Oracle table.
    /// Maps SQL Server data types to Oracle equivalents.
    /// Supports column mapping if provided (SQL Server column name -> Oracle column name).
    /// </summary>
    private async Task InsertIntoOracleAsync(string tableName, DataTable dataTable, Dictionary<string, string>? columnMappings = null, HashSet<string>? emptyToDashColumns = null, string? emptyValueReplacement = null, List<string>? additionalColumns = null, List<string>? additionalColumnsValues = null)
    {
        using (var oracleConnection = new OracleConnection(_oracleConnectionString))
        {
            await oracleConnection.OpenAsync();

            using (var transaction = oracleConnection.BeginTransaction())
            {
                try
                {
                    foreach (DataRow row in dataTable.Rows)
                    {
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

                            // 로그: 실행될 INSERT 문과 파라미터 값 출력
                            try
                            {
                                _logger.LogInformation($"[Executing] {insertQuery}");
                                foreach (OracleParameter p in command.Parameters)
                                {
                                    var displayVal = p.Value == DBNull.Value ? "NULL" : p.Value?.ToString();
                                    _logger.LogInformation($"  {p.ParameterName} = {displayVal}");
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning($"파라미터 로깅 중 오류 발생: {ex.Message}");
                            }

                            await command.ExecuteNonQueryAsync();
                        }
                    }

                    await transaction.CommitAsync();
                }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync();
                    _logger.LogError($"Transaction rolled back due to: {ex.Message}");
                    throw;
                }
            }
        }
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
                    _logger.LogInformation($"마이그레이션 시작: {mapping.SqlServerTableName} -> {mapping.OracleTableName}");
                    if (!string.IsNullOrEmpty(mapping.Description))
                    {
                        _logger.LogInformation($"  설명: {mapping.Description}");
                    }

                    // 대상 테이블 초기화 플래그가 설정된 경우 삭제(초기화)
                    if (mapping.DeleteTarget)
                    {
                        try
                        {
                            _logger.LogInformation($"  대상 테이블 초기화: {mapping.OracleTableName}");
                            await DeleteOracleTableAsync(mapping.OracleTableName);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning($"  대상 테이블 초기화 중 오류: {ex.Message}. 계속 진행합니다.");
                        }
                    }

                    await MigrateTableAsync(mapping.SqlServerTableName, mapping.OracleTableName, mapping.WhereCondition, mapping.ColumnMappings, mapping.EmptyToDashColumns, mapping.EmptyValueReplacement, mapping.AdditionalColumns, mapping.AdditionalColumnsValues);
                    successCount++;
                    _logger.LogInformation($"✓ {mapping.SqlServerTableName} 마이그레이션 완료");
                }
                catch (Exception ex)
                {
                    failureCount++;
                    _logger.LogError($"✗ {mapping.SqlServerTableName} 마이그레이션 실패: {ex.Message}");
                }
            }

            _logger.LogInformation($"========================================");
            _logger.LogInformation($"마이그레이션 완료: 성공 {successCount}개, 실패 {failureCount}개");
        }
        catch (Exception ex)
        {
            _logger.LogError($"Excel 매핑 기반 마이그레이션 중 오류 발생: {ex.Message}");
            throw;
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

