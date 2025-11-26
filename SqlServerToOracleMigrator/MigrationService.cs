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
    public async Task MigrateTableAsync(string sourceTable, string targetTable, string? whereCondition = null, Dictionary<string, string>? columnMappings = null, HashSet<string>? emptyToDashColumns = null, string? emptyValueReplacement = null)
    {
        try
        {
            _logger.LogInformation($"Starting migration of table '{sourceTable}' -> '{targetTable}'");
            if (columnMappings?.Count > 0)
            {
                _logger.LogInformation($"  Column mappings: {columnMappings.Count} columns mapped");
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
                    await MigrateBatchAsync(sourceTable, targetTable, offset, currentBatchSize, whereCondition, columnMappings, emptyToDashColumns, emptyValueReplacement);
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
    private async Task MigrateBatchAsync(string sourceTable, string targetTable, int offset, int batchSize, string? whereCondition = null, Dictionary<string, string>? columnMappings = null, HashSet<string>? emptyToDashColumns = null, string? emptyValueReplacement = null)
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
                    await InsertIntoOracleAsync(targetTable, dataTable, columnMappings, emptyToDashColumns, emptyValueReplacement);
                }
            }
        }
    }

    /// <summary>
    /// Inserts data into Oracle table.
    /// Maps SQL Server data types to Oracle equivalents.
    /// Supports column mapping if provided (SQL Server column name -> Oracle column name).
    /// </summary>
    private async Task InsertIntoOracleAsync(string tableName, DataTable dataTable, Dictionary<string, string>? columnMappings = null, HashSet<string>? emptyToDashColumns = null, string? emptyValueReplacement = null)
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

                        // 컬럼 매핑 적용: SQL Server 컬럼명 -> Oracle 컬럼명
                        var mappedColumns = validColumns.Select(c =>
                        {
                            var oracleColName = columnMappings?.ContainsKey(c.ColumnName) == true 
                                ? columnMappings[c.ColumnName] 
                                : c.ColumnName;
                            return new { Source = c.ColumnName, Target = oracleColName };
                        }).ToList();

                        var columnNames = string.Join(", ", mappedColumns.Select(c => $"\"{c.Target}\""));
                        var parameterNames = string.Join(", ", mappedColumns.Select((c, i) => $":p{i}"));

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

                            // Add parameters with type mapping (valid columns만)
                            for (int i = 0; i < validColumns.Count; i++)
                            {
                                var sourceColName = validColumns[i].ColumnName;
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

                    await MigrateTableAsync(mapping.SqlServerTableName, mapping.OracleTableName, mapping.WhereCondition, mapping.ColumnMappings, mapping.EmptyToDashColumns, mapping.EmptyValueReplacement);
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
                await MigrateTableAsync(mapping.SqlServerTableName, mapping.OracleTableName, mapping.WhereCondition, mapping.ColumnMappings, mapping.EmptyToDashColumns, mapping.EmptyValueReplacement);
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
}

