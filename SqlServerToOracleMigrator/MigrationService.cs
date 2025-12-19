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
    // 임시 진단용 PK 컬럼명 (필요 시 설정에서 주입 가능)
    private readonly string _diagnosticPkColumn = "TRANSACTION_SERIAL_NO";

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
    /// 중복 발생 시, 동일 키로 Oracle에 이미 저장된 행(일부)을 조회하여 동일 로그 파일에 추가 기록합니다.
    /// </summary>
    private void AppendExistingOracleRowsToDuplicateLog(
        string tableName,
        DataRow row,
        DataTable dataTable,
        OracleConnection oracleConnection,
        OracleTransaction transaction,
        Dictionary<string, string>? columnMappings,
        List<string>? primaryKeyColumns,
        string? constraintName,
        List<string>? oraclePkColumns,
        Dictionary<string,string>? targetToSourceMap,
        string? attemptedInsertSql,
        Dictionary<string, object?>? attemptedInsertParams)
    {
        try
        {
            var oracleColumns = GetOracleTableColumns(oracleConnection, transaction, tableName);
            var whereParts = new List<string>();
            var parameters = new List<OracleParameter>();
            var keysUsed = new List<object>();
            var resolvedOraclePkTargets = new List<string>();
            var resolvedSourceCols = new List<string>();
            var addedParamNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            Dictionary<string,string> insertTargetToExpr = new(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(attemptedInsertSql))
            {
                insertTargetToExpr = TryExtractInsertTargetToExpr(attemptedInsertSql!);
            }

            // Helper: add expression-based equality using attempted INSERT mapping (e.g., TO_CHAR(:p5,...))
            void TryAddByExpression(string targetCol)
            {
                if (string.IsNullOrWhiteSpace(targetCol)) return;
                if (!oracleColumns.Contains(targetCol.ToUpperInvariant())) return;
                if (!insertTargetToExpr.TryGetValue(targetCol, out var expr)) return;
                if (string.IsNullOrWhiteSpace(expr)) return;

                whereParts.Add($"\"{targetCol}\" = {expr}");

                // Bind any :pN parameters referenced in expr using attemptedInsertParams
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(expr, @":[a-zA-Z0-9_]+"))
                {
                    var pname = m.Value; // includes colon
                    if (addedParamNames.Contains(pname)) continue;
                    if (attemptedInsertParams != null && attemptedInsertParams.TryGetValue(pname, out var pval))
                    {
                        parameters.Add(CreateOracleParameter(pname, pval ?? DBNull.Value));
                        addedParamNames.Add(pname);
                    }
                    else if (attemptedInsertParams != null && attemptedInsertParams.TryGetValue(pname.TrimStart(':'), out pval))
                    {
                        parameters.Add(CreateOracleParameter(pname, pval ?? DBNull.Value));
                        addedParamNames.Add(pname);
                    }
                }
            }

            int keyIndex = 0;
            void TryAddKey(string srcCol, string targetCol)
            {
                // 소스 컬럼이 없으면, 대상 컬럼명이 DataTable에 존재하는지 확인하여 대체
                string effectiveSrc = srcCol;
                // 우선 실제 INSERT에 사용된 타겟→소스 매핑이 있으면 신뢰하여 사용
                if (targetToSourceMap != null && targetToSourceMap.TryGetValue(targetCol, out var mappedSrc))
                {
                    effectiveSrc = mappedSrc;
                }
                if (!dataTable.Columns.Contains(effectiveSrc))
                {
                    var tgtCandidate = string.IsNullOrWhiteSpace(targetCol) ? srcCol : targetCol;
                    if (dataTable.Columns.Contains(tgtCandidate))
                    {
                        effectiveSrc = tgtCandidate; // 역매핑 실패 대비: 대상명으로 소스값 참조
                    }
                    else
                    {
                        // 대소문자/오타 방지: 컬럼명 비교를 느슨하게 수행
                        var match = dataTable.Columns.Cast<DataColumn>()
                            .FirstOrDefault(c => string.Equals(c.ColumnName, tgtCandidate, StringComparison.OrdinalIgnoreCase)
                                              || string.Equals(c.ColumnName, srcCol, StringComparison.OrdinalIgnoreCase));
                        if (match != null)
                        {
                            effectiveSrc = match.ColumnName;
                        }
                        else
                        {
                            // 사용할 수 있는 소스 컬럼이 없으면, 시도한 INSERT의 값 표현식으로 대체
                            TryAddByExpression(targetCol);
                            return;
                        }
                    }
                }

                var target = string.IsNullOrWhiteSpace(targetCol) ? effectiveSrc : targetCol;
                if (!oracleColumns.Contains(target.ToUpperInvariant())) return;
                var pName = $":k{keyIndex++}";
                whereParts.Add($"\"{target}\" = {pName}");
                var v = row[effectiveSrc];
                parameters.Add(CreateOracleParameter(pName, v == DBNull.Value ? DBNull.Value : v));
                keysUsed.Add(new { Source = effectiveSrc, Target = target, Value = v == DBNull.Value ? null : v });
            }

            // 0) Excel에서 명시한 Oracle PK 컬럼을 최우선 사용
            if (oraclePkColumns != null && oraclePkColumns.Count > 0)
            {
                foreach (var tgtColRaw in oraclePkColumns)
                {
                    var tgtCol = tgtColRaw.Trim().Trim('"');
                    string srcCol = tgtCol;
                    if (columnMappings != null)
                    {
                        var match = columnMappings.FirstOrDefault(kv => string.Equals(kv.Value, tgtCol, StringComparison.OrdinalIgnoreCase));
                        if (!string.IsNullOrEmpty(match.Key)) srcCol = match.Key;
                    }
                    var before = whereParts.Count;
                    TryAddKey(srcCol, tgtCol);
                    if (whereParts.Count == before)
                    {
                        // Try to add by expression if mapping missing
                        TryAddByExpression(tgtCol);
                        // 최후 보강: 대상 컬럼이 DataTable에 있으면 그 값 사용
                        if (whereParts.Count == before && oracleColumns.Contains(tgtCol.ToUpperInvariant()) && dataTable.Columns.Contains(tgtCol))
                        {
                            var pName = $":k{keyIndex++}";
                            whereParts.Add($"\"{tgtCol}\" = {pName}");
                            var v = row[tgtCol];
                            parameters.Add(CreateOracleParameter(pName, v == DBNull.Value ? DBNull.Value : v));
                            keysUsed.Add(new { Source = tgtCol, Target = tgtCol, Value = v == DBNull.Value ? null : v });
                        }
                    }
                    if (oracleColumns.Contains(tgtCol.ToUpperInvariant()))
                    {
                        resolvedOraclePkTargets.Add(tgtCol);
                        // 실제 사용 소스명을 기록 (targetToSource 우선)
                        if (targetToSourceMap != null && targetToSourceMap.TryGetValue(tgtCol, out var mappedSrc))
                            resolvedSourceCols.Add(mappedSrc);
                        else
                            resolvedSourceCols.Add(srcCol);
                    }
                }
            }

            // Excel V열이 비어있다면, Oracle 메타데이터에서 PK 자동 추출
            if ((oraclePkColumns == null || oraclePkColumns.Count == 0) && whereParts.Count == 0)
            {
                var pkTargets = GetOraclePrimaryKeyColumns(oracleConnection, transaction, tableName);
                foreach (var tgtCol in pkTargets)
                {
                    string srcCol = tgtCol;
                    if (columnMappings != null)
                    {
                        var match = columnMappings.FirstOrDefault(kv => string.Equals(kv.Value, tgtCol, StringComparison.OrdinalIgnoreCase));
                        if (!string.IsNullOrEmpty(match.Key)) srcCol = match.Key;
                    }
                    TryAddKey(srcCol, tgtCol);
                    if (oracleColumns.Contains(tgtCol.ToUpperInvariant()))
                    {
                        resolvedOraclePkTargets.Add(tgtCol);
                        if (targetToSourceMap != null && targetToSourceMap.TryGetValue(tgtCol, out var mappedSrc))
                            resolvedSourceCols.Add(mappedSrc);
                        else
                            resolvedSourceCols.Add(srcCol);
                    }
                }
            }

            // 1) 제약조건 이름이 있으면 해당 제약조건의 컬럼들을 우선 사용
            if (!string.IsNullOrWhiteSpace(constraintName))
            {
                var consCols = GetConstraintColumns(oracleConnection, transaction, tableName, constraintName);
                foreach (var tgtCol in consCols)
                {
                    // 대상 컬럼을 역매핑해서 소스 컬럼 찾기
                    string srcCol = tgtCol;
                    if (columnMappings != null)
                    {
                        var match = columnMappings.FirstOrDefault(kv => string.Equals(kv.Value, tgtCol, StringComparison.OrdinalIgnoreCase));
                        if (!string.IsNullOrEmpty(match.Key)) srcCol = match.Key;
                    }
                    TryAddKey(srcCol, tgtCol);
                }
            }

            // 2) PK 컬럼 정보가 있으면 사용
            if (whereParts.Count == 0 && primaryKeyColumns != null && primaryKeyColumns.Count > 0)
            {
                foreach (var pk in primaryKeyColumns)
                {
                    var tgt = (columnMappings != null && columnMappings.TryGetValue(pk, out var m)) ? m : pk;
                    TryAddKey(pk, tgt);
                }
            }

            if (whereParts.Count == 0 && dataTable.Columns.Contains(_diagnosticPkColumn))
            {
                var tgtDiag = (columnMappings != null && columnMappings.TryGetValue(_diagnosticPkColumn, out var md)) ? md : _diagnosticPkColumn;
                TryAddKey(_diagnosticPkColumn, tgtDiag);
            }

            if (whereParts.Count == 0)
            {
                foreach (var hc in new[] { "HIST_SEQNO", "TRANSACTION_SERIAL_NO" })
                {
                    var tgt = (columnMappings != null && columnMappings.TryGetValue(hc, out var m)) ? m : hc;
                    var before = whereParts.Count;
                    TryAddKey(hc, tgt);
                    if (whereParts.Count > before) break;
                }
            }

            // 중복된 대상 컬럼 제거 (같은 컬럼이 여러 경로로 추가되었을 경우)
            if (whereParts.Count > 1)
            {
                var distinct = new List<string>();
                var seenTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var wp in whereParts)
                {
                    // wp 형태: "COL" = :kN
                    var idxQuote = wp.IndexOf('"');
                    var idxQuote2 = wp.IndexOf('"', idxQuote + 1);
                    var target = (idxQuote >= 0 && idxQuote2 > idxQuote) ? wp.Substring(idxQuote + 1, idxQuote2 - idxQuote - 1) : wp;
                    if (seenTargets.Add(target)) distinct.Add(wp);
                }
                whereParts = distinct;
            }

            var whereClause = whereParts.Count > 0 ? string.Join(" AND ", whereParts) : string.Empty;
            var rows = new List<Dictionary<string, object?>>();
            string? selectError = null;
            string? selectSql = null;

            try
            {
                selectSql = whereParts.Count > 0
                    ? $"SELECT * FROM {tableName} WHERE {whereClause} FETCH FIRST 5 ROWS ONLY"
                    : $"SELECT * FROM {tableName} WHERE 1=0";

                using (var cmd = new OracleCommand(selectSql, oracleConnection))
                {
                    cmd.Transaction = transaction;
                    cmd.BindByName = true;
                    foreach (var p in parameters) cmd.Parameters.Add(p);
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            var dict = new Dictionary<string, object?>();
                            for (int i = 0; i < r.FieldCount; i++)
                            {
                                var col = r.GetName(i);
                                object? val = r.IsDBNull(i) ? null : r.GetValue(i);
                                if (val is DateTime dt) val = dt.ToString("yyyy-MM-dd HH:mm:ss.fffffff");
                                dict[col] = val;
                            }
                            rows.Add(dict);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                selectError = ex.Message;
            }

            string timestamp = DateTime.Now.ToString("yyyyMMdd");
            string logFileName = $"{tableName.Replace(".", "_")}_{timestamp}_duplicates.log";
            string logFilePath = Path.Combine(_duplicateLogDirectory, logFileName);

            var rowData = new Dictionary<string, object?>();
            foreach (DataColumn c in dataTable.Columns)
            {
                var val = row[c];
                rowData[c.ColumnName] = val == DBNull.Value ? null : val;
            }
            string? insdttmAnalysis = null;
            if (dataTable.Columns.Contains("INSDTTM") && row["INSDTTM"] != DBNull.Value && row["INSDTTM"] is DateTime dtIn)
            {
                insdttmAnalysis = $"INSDTTM Raw: {dtIn:yyyy-MM-dd HH:mm:ss.fffffff} | TO_CHAR 시뮬레이션: {dtIn:yyyyMMddHHmmssffffff}00";
            }

            var mergedEntry = new
            {
                Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                Table = tableName,
                Data = rowData,
                Analysis = insdttmAnalysis,
                Warning = "Oracle PK 충돌 - 동일 키 존재",
                Keys = keysUsed,
                ExistingOracleRows = rows,
                ExistingOracleRowsCount = rows.Count,
                ExistingOracleWhere = whereClause,
                ExistingOracleError = selectError,
                ConstraintName = constraintName,
                ResolvedOraclePkTargets = resolvedOraclePkTargets,
                ResolvedSourceCols = resolvedSourceCols,
                ExistingOracleSelect = selectSql,
                AttemptedInsertSql = attemptedInsertSql,
                AttemptedInsertParams = attemptedInsertParams
            };

            string jsonLine = JsonSerializer.Serialize(mergedEntry, new JsonSerializerOptions
            {
                WriteIndented = false,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
            lock (this)
            {
                File.AppendAllText(logFilePath, jsonLine + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // swallow to avoid noisy console logs
        }
    }

    // Attempt to extract mapping of target column -> value expression from an INSERT or MERGE SQL
    // Supports patterns:
    //  - INSERT INTO table ("COL1","COL2") VALUES (expr1, expr2)
    //  - MERGE ... WHEN NOT MATCHED THEN INSERT ("COL1","COL2") VALUES (expr1, expr2)
    private Dictionary<string,string> TryExtractInsertTargetToExpr(string sql)
    {
        var map = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            int insertIdx = sql.IndexOf("INSERT", StringComparison.OrdinalIgnoreCase);
            if (insertIdx < 0) return map;

            // Find first '(' after INSERT for column list
            int colStart = sql.IndexOf('(', insertIdx);
            if (colStart < 0) return map;

            int colEnd = FindMatchingParen(sql, colStart);
            if (colEnd < 0) return map;

            // Find VALUES and the following '(' for values list
            int valuesIdx = sql.IndexOf("VALUES", colEnd, StringComparison.OrdinalIgnoreCase);
            if (valuesIdx < 0) return map;
            int valStart = sql.IndexOf('(', valuesIdx);
            if (valStart < 0) return map;
            int valEnd = FindMatchingParen(sql, valStart);
            if (valEnd < 0) return map;

            var colsSegment = sql.Substring(colStart + 1, colEnd - colStart - 1);
            var valsSegment = sql.Substring(valStart + 1, valEnd - valStart - 1);

            var cols = SplitTopLevel(colsSegment);
            var vals = SplitTopLevel(valsSegment);
            if (cols.Count != vals.Count) return map;

            for (int i = 0; i < cols.Count; i++)
            {
                var col = cols[i].Trim().Trim('"');
                var expr = vals[i].Trim();
                if (!string.IsNullOrWhiteSpace(col))
                    map[col] = expr;
            }
        }
        catch
        {
            // ignore parsing errors
        }
        return map;
    }

    private static int FindMatchingParen(string s, int openIndex)
    {
        int depth = 0;
        for (int i = openIndex; i < s.Length; i++)
        {
            if (s[i] == '(') depth++;
            else if (s[i] == ')')
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
    }

    private static List<string> SplitTopLevel(string s)
    {
        var parts = new List<string>();
        var sb = new StringBuilder();
        int depth = 0;
        bool inString = false;
        char stringChar = '\'';
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (inString)
            {
                sb.Append(c);
                if (c == stringChar)
                {
                    // Handle escaped quotes '' inside string
                    if (i + 1 < s.Length && s[i + 1] == stringChar)
                    {
                        sb.Append(s[i + 1]);
                        i++;
                    }
                    else
                    {
                        inString = false;
                    }
                }
                continue;
            }

            if (c == '\'' || c == '"')
            {
                inString = true;
                stringChar = c;
                sb.Append(c);
                continue;
            }

            if (c == '(') { depth++; sb.Append(c); continue; }
            if (c == ')') { depth--; sb.Append(c); continue; }

            if (c == ',' && depth == 0)
            {
                parts.Add(sb.ToString());
                sb.Clear();
                continue;
            }

            sb.Append(c);
        }
        if (sb.Length > 0) parts.Add(sb.ToString());
        return parts;
    }

    // 대상 테이블의 컬럼 목록을 조회 (대소문자 무시 비교를 위해 모두 대문자로 수집)
    private HashSet<string> GetOracleTableColumns(OracleConnection connection, OracleTransaction tx, string fullTableName)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string owner;
            string table = fullTableName;
            if (fullTableName.Contains('.'))
            {
                var parts = fullTableName.Split('.');
                owner = parts[0].Trim('"').ToUpperInvariant();
                table = parts[1];
            }
            else
            {
                // 현재 스키마 조회 (CURRENT_SCHEMA가 없으면 USER 사용)
                using (var ownerCmd = new OracleCommand("SELECT SYS_CONTEXT('USERENV','CURRENT_SCHEMA') FROM DUAL", connection))
                {
                    ownerCmd.Transaction = tx;
                    var schemaObj = ownerCmd.ExecuteScalar();
                    var schema = schemaObj?.ToString();
                    if (string.IsNullOrWhiteSpace(schema))
                    {
                        using (var userCmd = new OracleCommand("SELECT USER FROM DUAL", connection))
                        {
                            userCmd.Transaction = tx;
                            schema = userCmd.ExecuteScalar()?.ToString();
                        }
                    }
                    owner = (schema ?? string.Empty).ToUpperInvariant();
                }
            }
            table = table.Trim('"').ToUpperInvariant();

            using (var cmd = new OracleCommand("SELECT COLUMN_NAME FROM ALL_TAB_COLUMNS WHERE OWNER = :OWN AND TABLE_NAME = :TN", connection))
            {
                cmd.Transaction = tx;
                cmd.Parameters.Add(new OracleParameter(":OWN", owner));
                cmd.Parameters.Add(new OracleParameter(":TN", table));
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        set.Add(r.GetString(0).ToUpperInvariant());
                    }
                }
            }
        }
        catch
        {
            // 조회 실패 시 빈 집합 반환 (WHERE 구성 시 존재 확인이 스킵됨)
        }
        return set;
    }

    // 제약조건 이름으로 컬럼 목록 조회 (대상 테이블 컬럼명 기준 반환)
    private List<string> GetConstraintColumns(OracleConnection connection, OracleTransaction tx, string fullTableName, string constraintName)
    {
        var cols = new List<string>();
        try
        {
            // OWNER 및 TABLE_NAME 파싱 (스키마 미지정 시 CURRENT_SCHEMA/USER 사용)
            string owner;
            string table = fullTableName;
            if (fullTableName.Contains('.'))
            {
                var parts = fullTableName.Split('.');
                owner = parts[0].Trim('"').ToUpperInvariant();
                table = parts[1];
            }
            else
            {
                using (var ownerCmd = new OracleCommand("SELECT SYS_CONTEXT('USERENV','CURRENT_SCHEMA') FROM DUAL", connection))
                {
                    ownerCmd.Transaction = tx;
                    var schemaObj = ownerCmd.ExecuteScalar();
                    var schema = schemaObj?.ToString();
                    if (string.IsNullOrWhiteSpace(schema))
                    {
                        using (var userCmd = new OracleCommand("SELECT USER FROM DUAL", connection))
                        {
                            userCmd.Transaction = tx;
                            schema = userCmd.ExecuteScalar()?.ToString();
                        }
                    }
                    owner = (schema ?? string.Empty).ToUpperInvariant();
                }
            }
            table = table.Trim('"').ToUpperInvariant();

            using (var cmd = new OracleCommand("SELECT COLUMN_NAME FROM ALL_CONS_COLUMNS WHERE OWNER = :OWN AND TABLE_NAME = :TN AND CONSTRAINT_NAME = :CN ORDER BY POSITION", connection))
            {
                cmd.Transaction = tx;
                cmd.Parameters.Add(new OracleParameter(":OWN", owner));
                cmd.Parameters.Add(new OracleParameter(":TN", table));
                cmd.Parameters.Add(new OracleParameter(":CN", constraintName.ToUpperInvariant()));
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        cols.Add(r.GetString(0).Trim('"'));
                    }
                }
            }
        }
        catch
        {
            // 조회 실패 시 빈 목록 반환
        }
        return cols;
    }

    // Oracle에서 대상 테이블의 Primary Key 컬럼 목록을 조회 (OWNER + TABLE_NAME 기준)
    private List<string> GetOraclePrimaryKeyColumns(OracleConnection connection, OracleTransaction tx, string fullTableName)
    {
        var cols = new List<string>();
        try
        {
            string owner;
            string table = fullTableName;
            if (fullTableName.Contains('.'))
            {
                var parts = fullTableName.Split('.');
                owner = parts[0].Trim('"').ToUpperInvariant();
                table = parts[1];
            }
            else
            {
                using (var ownerCmd = new OracleCommand("SELECT SYS_CONTEXT('USERENV','CURRENT_SCHEMA') FROM DUAL", connection))
                {
                    ownerCmd.Transaction = tx;
                    var schemaObj = ownerCmd.ExecuteScalar();
                    var schema = schemaObj?.ToString();
                    if (string.IsNullOrWhiteSpace(schema))
                    {
                        using (var userCmd = new OracleCommand("SELECT USER FROM DUAL", connection))
                        {
                            userCmd.Transaction = tx;
                            schema = userCmd.ExecuteScalar()?.ToString();
                        }
                    }
                    owner = (schema ?? string.Empty).ToUpperInvariant();
                }
            }
            table = table.Trim('"').ToUpperInvariant();

            string sql = @"
                SELECT acc.COLUMN_NAME
                FROM ALL_CONSTRAINTS ac
                JOIN ALL_CONS_COLUMNS acc
                  ON ac.OWNER = acc.OWNER
                 AND ac.CONSTRAINT_NAME = acc.CONSTRAINT_NAME
                WHERE ac.OWNER = :OWN
                  AND ac.TABLE_NAME = :TN
                  AND ac.CONSTRAINT_TYPE = 'P'
                ORDER BY acc.POSITION";

            using (var cmd = new OracleCommand(sql, connection))
            {
                cmd.Transaction = tx;
                cmd.BindByName = true;
                cmd.Parameters.Add(new OracleParameter(":OWN", owner));
                cmd.Parameters.Add(new OracleParameter(":TN", table));
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        cols.Add(r.GetString(0).Trim('"'));
                    }
                }
            }
        }
        catch
        {
            // 조회 실패 시 빈 목록 반환
        }
        return cols;
    }

    /// <summary>
    /// 임시 진단: 지정된 PK 컬럼으로 Oracle 대상 테이블에 존재 여부를 COUNT로 확인하고 로그합니다.
    /// </summary>
    private void LogPkExistenceDiagnostic(
        string tableName,
        DataRow row,
        DataTable dataTable,
        OracleConnection oracleConnection,
        OracleTransaction transaction,
        string contextTag,
        Dictionary<string, string>? columnMappings = null,
        List<string>? primaryKeyColumns = null)
    {
        try
        {
            // WHERE 절 작성: 우선 전달된 PK 컬럼 조합 사용, 없으면 진단 키 사용
            var whereParts = new List<string>();
            var parameters = new List<OracleParameter>();

            if (primaryKeyColumns != null && primaryKeyColumns.Count > 0)
            {
                int k = 0;
                foreach (var pkSrc in primaryKeyColumns)
                {
                    if (!dataTable.Columns.Contains(pkSrc)) continue;
                    var targetCol = (columnMappings != null && columnMappings.TryGetValue(pkSrc, out var mapped)) ? mapped : pkSrc;
                    var paramName = $":k{k}";
                    whereParts.Add($"\"{targetCol}\" = {paramName}");
                    var value = row[pkSrc];
                    parameters.Add(CreateOracleParameter(paramName, value == DBNull.Value ? DBNull.Value : value));
                    k++;
                }
            }

            if (whereParts.Count == 0)
            {
                var pkCol = _diagnosticPkColumn;
                if (!dataTable.Columns.Contains(pkCol))
                {
                    _logger.LogWarning($"[임시진단][{contextTag}] 키 컬럼을 찾지 못해 SELECT COUNT 생략");
                    return;
                }
                var targetCol = (columnMappings != null && columnMappings.TryGetValue(pkCol, out var mapped)) ? mapped : pkCol;
                var paramName = ":k0";
                whereParts.Add($"\"{targetCol}\" = {paramName}");
                var value = row[pkCol];
                parameters.Add(CreateOracleParameter(paramName, value == DBNull.Value ? DBNull.Value : value));
            }

            var whereClause = string.Join(" AND ", whereParts);
            string selectSql = $"SELECT COUNT(*) FROM {tableName} WHERE {whereClause}";
            using (var checkCmd = new OracleCommand(selectSql, oracleConnection))
            {
                checkCmd.Transaction = transaction;
                checkCmd.BindByName = true;
                foreach (var p in parameters) checkCmd.Parameters.Add(p);
                var cntObj = checkCmd.ExecuteScalar();
                int cnt = cntObj == null || cntObj == DBNull.Value ? 0 : Convert.ToInt32(cntObj);
                _logger.LogInformation($"[임시진단][{contextTag}] ORACLE 존재 COUNT: {cnt}  (WHERE: {whereClause})");
            }
        }
        catch (Exception ex)
        {
            _logger.LogInformation($"[임시진단][{contextTag}] PK COUNT 체크 오류: {ex.Message}");
        }
    }

    /// <summary>
    /// OracleParameter를 생성하며 DateTime은 정밀도를 유지하도록 TIMESTAMP 형식으로 설정합니다.
    /// </summary>
    private OracleParameter CreateOracleParameter(string name, object? value)
    {
        if (value == null || value == DBNull.Value)
        {
            return new OracleParameter(name, DBNull.Value);
        }

        if (value is DateTime dt)
        {
            return new OracleParameter(name, OracleDbType.TimeStamp)
            {
                Value = dt
            };
        }

        if (value is DateTimeOffset dto)
        {
            return new OracleParameter(name, OracleDbType.TimeStampTZ)
            {
                Value = dto.UtcDateTime
            };
        }

        return new OracleParameter(name, value);
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
            // 중복 진단을 위해 PK 컬럼을 전 구간에서 확보
            List<string> primaryKeyColumns = new();
            
            // 1. Excel에서 명시적으로 지정한 정렬 컬럼 사용 (최우선)
            if (!string.IsNullOrWhiteSpace(orderByColumns))
            {
                orderByClause = $"ORDER BY {orderByColumns}";
                _logger.LogInformation($"✓ Excel S열에서 지정한 정렬 컬럼 사용: {orderByColumns}");
                _logger.LogInformation($"✓ ORDER BY 절: {orderByClause}");
                // 동시에 PK 컬럼도 조회하여 중복 로그 키로 활용
                try
                {
                    primaryKeyColumns = await GetPrimaryKeyColumnsAsync(sourceTable);
                    if (primaryKeyColumns.Count > 0)
                    {
                        _logger.LogInformation($"(진단용) PK 조회: {string.Join(", ", primaryKeyColumns)}");
                    }
                }
                catch { }
            }
            // 2. Primary Key 자동 조회
            else
            {
                _logger.LogInformation($"Excel S열이 비어있음. Primary Key 자동 조회 시작...");
                primaryKeyColumns = await GetPrimaryKeyColumnsAsync(sourceTable);
                
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
                    // Primary Key 컬럼을 배치 처리에도 전달하여 중복 시 Oracle 기존행 조회에 활용
                    var (successCount, skipCount, processedCount) = await MigrateBatchAsync(
                        sourceTable,
                        targetTable,
                        offset,
                        currentBatchSize,
                        whereCondition,
                        columnMappings,
                        emptyToDashColumns,
                        emptyValueReplacement,
                        additionalColumns,
                        additionalColumnsValues,
                        orderByClause,
                        primaryKeyColumns);
                    
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
    private async Task<(int successCount, int skipCount, int totalCount)> MigrateBatchAsync(
        string sourceTable,
        string targetTable,
        int offset,
        int batchSize,
        string? whereCondition = null,
        Dictionary<string, string>? columnMappings = null,
        HashSet<string>? emptyToDashColumns = null,
        string? emptyValueReplacement = null,
        List<string>? additionalColumns = null,
        List<string>? additionalColumnsValues = null,
        string? orderByClause = null,
        List<string>? primaryKeyColumns = null)
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
                    
                    var (successCount, skipCount, totalCount) = await InsertIntoOracleAsync(
                        targetTable,
                        dataTable,
                        columnMappings,
                        emptyToDashColumns,
                        emptyValueReplacement,
                        additionalColumns,
                        additionalColumnsValues,
                        primaryKeyColumns);
                    
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
    private async Task<(int successCount, int skipCount, int totalCount)> InsertIntoOracleAsync(
        string tableName,
        DataTable dataTable,
        Dictionary<string, string>? columnMappings = null,
        HashSet<string>? emptyToDashColumns = null,
        string? emptyValueReplacement = null,
        List<string>? additionalColumns = null,
        List<string>? additionalColumnsValues = null,
        List<string>? primaryKeyColumns = null)
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
                        var extraParams = new List<OracleParameter>();
                        int addParamIndex = columnsToInsert.Count;

                        // 추가 컬럼(L열) 값 또는 식 처리
                        if (additionalColumns != null && additionalColumns.Count > 0)
                        {
                            for (int ai = 0; ai < additionalColumns.Count; ai++)
                            {
                                allTargetColumns.Add($"\"{additionalColumns[ai]}\"");
                                
                                if (ai < (additionalColumnsValues?.Count ?? 0))
                                {
                                    var rawExpr = additionalColumnsValues![ai];
                                    var builtExpr = BuildAdditionalExpressionForInsert(rawExpr, mappedColumns.Cast<object>().ToList(), validColumns, row, extraParams, ref addParamIndex);
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
                            command.BindByName = true;

                            // Add parameters with type mapping (columnsToInsert만 사용)
                            var attemptedParams = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
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

                                var param = CreateOracleParameter($":p{i}", value ?? DBNull.Value);
                                command.Parameters.Add(param);
                                attemptedParams[param.ParameterName] = param.Value == DBNull.Value ? null : param.Value;
                            }
                            // Add extra parameters generated by L expressions
                            foreach (var p in extraParams)
                            {
                                command.Parameters.Add(p);
                                attemptedParams[p.ParameterName] = p.Value == DBNull.Value ? null : p.Value;
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

                                // 실제 중복행(Oracle에서 조회 가능)만 중복 로그에 병합 기록
                                try
                                {
                                    // ORA-00001 메시지에서 제약조건 이름 추출: "(OWNER.CONSTRAINT)"
                                    string? constraintName = null;
                                    var msg = oracleEx.Message;
                                    var start = msg.IndexOf('(');
                                    var end = msg.IndexOf(')');
                                    if (start >= 0 && end > start)
                                    {
                                        var within = msg.Substring(start + 1, end - start - 1);
                                        var parts = within.Split('.');
                                        constraintName = parts.Length == 2 ? parts[1] : within;
                                    }
                                    AppendExistingOracleRowsToDuplicateLog(
                                        tableName,
                                        row,
                                        dataTable,
                                        oracleConnection,
                                        transaction,
                                        columnMappings,
                                        primaryKeyColumns,
                                        constraintName,
                                        null,
                                        null,
                                        insertQuery,
                                        attemptedParams);
                                }
                                catch { }
                                
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
                                    _logger.LogWarning($"[{tableName}] 중복 키로 인해 행 건너뜀 (누적: {skipCount}개)");
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
    public async Task MigrateWithMappingAsync(List<TableMapping> mappings, bool useMerge = false)
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

                    var (insertedCount, skippedCount, totalProcessed) = MigrateTableSync(
                        mapping.SqlServerTableName,
                        mapping.OracleTableName,
                        mapping.WhereCondition,
                        mapping.ColumnMappings,
                        mapping.EmptyToDashColumns,
                        mapping.EmptyValueReplacement,
                        mapping.AdditionalColumns,
                        mapping.AdditionalColumnsValues,
                        mapping.OrderByColumns,
                        useMerge,
                        mapping.OraclePkColumns);
                    
                    // 완료 시간 및 통계 기록
                    mapping.EndTime = DateTime.Now;
                    mapping.RecordCount = (int)insertedCount;
                    mapping.SkippedCount = skippedCount;
                    mapping.TotalProcessed = (int)totalProcessed;
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

                        // 추가 컬럼(L열) 값 또는 식 처리
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
    private string BuildAdditionalExpressionForInsert(string rawExpr, List<object> mappedColumns, List<DataColumn> validColumns, DataRow row, List<OracleParameter> parameters, ref int nextParamIndex)
    {
        if (string.IsNullOrWhiteSpace(rawExpr))
            return "NULL";

        // 간단한 안전 검사
        var lower = rawExpr.ToLowerInvariant();
        if (lower.Contains(";") || lower.Contains("--") || lower.Contains("/*") || lower.Contains("*/"))
            throw new InvalidOperationException("추가 식에 허용되지 않는 문자가 포함되어 있습니다.");

        // {Col} 토큰 맵 생성: {Col} → :pN 파라미터 (기존 매핑 먼저)
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
            if (!paramMap.TryGetValue(col, out var pname))
            {
                // 매핑에 없는 {Col}: 새로운 :pN 파라미터를 생성하고 값 바인딩
                if (!validColumns.Contains(validColumns.Cast<DataColumn>().FirstOrDefault(dc => string.Equals(dc.ColumnName, col, StringComparison.OrdinalIgnoreCase))))
                {
                    // DataTable에 없으면 NULL 처리
                    result = result.Replace(m.Value, "NULL");
                    continue;
                }
                // DataTable에서 실제 값을 찾아 파라미터 생성
                var dc = validColumns.Cast<DataColumn>().FirstOrDefault(x => string.Equals(x.ColumnName, col, StringComparison.OrdinalIgnoreCase));
                if (dc != null)
                {
                    var p = $":p{nextParamIndex++}";
                    var value = row[dc.ColumnName];
                    parameters.Add(CreateOracleParameter(p, value == DBNull.Value ? DBNull.Value : value));
                    pname = p;
                    paramMap[col] = p;
                }
                else
                {
                    result = result.Replace(m.Value, "NULL");
                    continue;
                }
            }
            result = result.Replace(m.Value, pname);
        }

        return result;
    }

    #region 동기(Sync) 방식 마이그레이션 메서드

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
        string? orderByColumns = null,
        bool useMerge = false,
        List<string>? oraclePkColumns = null)
    {
        try
        {
            _logger.LogInformation($"\n========== 동기 방식 마이그레이션 시작 ==========");
            _logger.LogInformation($"Source: {sourceTable} → Target: {targetTable}");
            
            // 1. 전체 행 수 조회 (동기)
            long totalRows = GetRowCountSync(sourceTable, whereCondition);
            
            if (totalRows == 0)
            {
                _logger.LogWarning($"테이블 {sourceTable}에 데이터가 없습니다.");
                return (0, 0, 0);
            }
            
            _logger.LogInformation($"총 {totalRows:N0}개 행을 {_batchSize:N0}개씩 처리합니다.");
            
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
                
                _logger.LogInformation($"\n[배치 {batchNumber}] 시작 (오프셋: {processedRows:N0}, 크기: {batchSize:N0})");
                
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
                        orderByClause,
                        primaryKeyColumns,
                        useMerge,
                        oraclePkColumns
                    );
                    
                    totalSuccess += successCount;
                    totalSkipped += skipCount;
                    
                    // ✓ **실제 읽은 행 수(readCount)**로 다음 OFFSET 계산
                    // 중복이 있어도 OFFSET은 계속 증가해야 무한 반복 방지
                    processedRows += readCount;
                    
                    _logger.LogInformation($"[배치 {batchNumber}] 완료 (읽음: {readCount:N0}, 성공: {successCount:N0}, 중복: {skipCount:N0}, 누적: {totalSuccess:N0})");
                    
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
            _logger.LogInformation($"  총 처리: {processedRows:N0}");
            _logger.LogInformation($"  성공: {totalSuccess:N0}");
            _logger.LogInformation($"  중복 건너뜀: {totalSkipped:N0}");
            _logger.LogInformation($"==========================================\n");
            
            return (totalSuccess, totalSkipped, processedRows);
        }
        catch (Exception ex)
        {
            _logger.LogError($"마이그레이션 실패: {ex.Message}");
            throw;
        }
    }

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
        string? orderByClause = null,
        List<string>? primaryKeyColumns = null,
        bool useMerge = false,
        List<string>? oraclePkColumns = null)
    {
        try
        {
            _logger.LogInformation($"[MigrateBatchSync] 시작 - 오프셋: {offset:N0}, 크기: {batchSize:N0}");
            
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
            
            _logger.LogInformation($"[MigrateBatchSync] {dataTable.Rows.Count:N0}개 행 읽음");
            
            // 2. 동기 방식으로 Oracle에 INSERT
            var (successCount, skipCount) = InsertIntoOracleSync(
                targetTable,
                dataTable,
                columnMappings,
                emptyToDashColumns,
                emptyValueReplacement,
                additionalColumns,
                additionalColumnsValues,
                primaryKeyColumns,
                useMerge,
                oraclePkColumns);
            
            _logger.LogInformation($"[MigrateBatchSync] 완료 (성공: {successCount:N0}, 중복: {skipCount:N0})");
            
            return (successCount, skipCount, dataTable.Rows.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError($"[MigrateBatchSync] 오류: {ex.Message}");
            throw;
        }
    }

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
            
            _logger.LogDebug($"[ReadBatchSync] 쿼리: {query}");
            
            using (var command = new SqlCommand(query, sqlConnection))
            {
                command.CommandTimeout = _commandTimeout;
                
                // 동기 실행
                using (var reader = command.ExecuteReader())
                {
                    var dataTable = new DataTable();
                    // 동기 로드
                    dataTable.Load(reader);
                    
                    _logger.LogInformation($"[ReadBatchSync] {dataTable.Rows.Count:N0}개 행 로드 완료");
                    return dataTable;
                }
            }
        }
    }

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
        List<string>? additionalColumnsValues = null,
        List<string>? primaryKeyColumns = null,
        bool useMerge = false,
        List<string>? oraclePkColumns = null)
    {
        _logger.LogInformation($"[InsertIntoOracleSync] 시작 - 테이블: {tableName}, 행: {dataTable.Rows.Count:N0}");
        
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
                    int rowIndex = 0;
                    foreach (DataRow row in dataTable.Rows)
                    {
                        rowIndex++;
                        
                        // 컬럼명 유효성 검사
                        var validColumns = dataTable.Columns.Cast<DataColumn>()
                            .Where(c => !string.IsNullOrWhiteSpace(c.ColumnName) && c.ColumnName.All(ch => char.IsLetterOrDigit(ch) || ch == '_'))
                            .ToList();

                        if (validColumns.Count == 0)
                        {
                            _logger.LogWarning($"[{tableName}] 컬럼명이 비어있거나 유효하지 않아 INSERT를 건너뜁니다.");
                            continue;
                        }

                        // G/H열 매핑이 지정된 경우
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

                        // 컬럼 매핑 적용
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
                        var parameters = new List<OracleParameter>();
                        int addParamIndex = columnsToInsert.Count;

                        // 기본 컬럼 파라미터 생성
                        for (int i = 0; i < columnsToInsert.Count; i++)
                        {
                            var sourceColName = columnsToInsert[i].ColumnName;
                            object value = row[sourceColName];
                            
                            // 공백 값 처리
                            if (emptyToDashColumns?.Contains(sourceColName) == true && value != DBNull.Value)
                            {
                                string strValue = value.ToString() ?? "";
                                if (string.IsNullOrWhiteSpace(strValue))
                                {
                                    value = emptyValueReplacement ?? "-";
                                }
                            }
                            
                            if (value == DBNull.Value)
                            {
                                parameters.Add(CreateOracleParameter($":p{i}", DBNull.Value));
                            }
                            else
                            {
                                parameters.Add(CreateOracleParameter($":p{i}", value));
                            }
                        }

                        // 추가 컬럼(L열) 값 또는 식 처리
                        if (additionalColumns != null && additionalColumns.Count > 0)
                        {
                            int paramIndex = columnsToInsert.Count;
                            for (int ai = 0; ai < additionalColumns.Count; ai++)
                            {
                                allTargetColumns.Add($"\"{additionalColumns[ai]}\"");
                                
                                if (ai < (additionalColumnsValues?.Count ?? 0))
                                {
                                    var rawExpr = additionalColumnsValues![ai];
                                    var builtExpr = BuildAdditionalExpressionForInsert(rawExpr, mappedColumns.Cast<object>().ToList(), validColumns, row, parameters, ref addParamIndex);
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

                        // MERGE(증분) 또는 INSERT 실행
                        string sqlToExecute;
                        bool executeAsMerge = useMerge;

                        // MERGE 쿼리 구성
                        if (useMerge)
                        {
                            try
                            {
                                var pkSrcList = (primaryKeyColumns ?? new List<string>());
                                // 매핑된 키 타겟 컬럼 이름 목록
                                var srcToIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                                for (int i = 0; i < mappedColumns.Count; i++)
                                {
                                    srcToIndex[mappedColumns[i].Source] = i;
                                }

                                // 키 구성 검증: 모든 키가 파라미터 매핑 가능해야 함
                                var keyPairs = new List<(string Target, string ParamName)>();
                                foreach (var pkSrc in pkSrcList)
                                {
                                    var pkTarget = (columnMappings != null && columnMappings.ContainsKey(pkSrc)) ? columnMappings[pkSrc] : pkSrc;
                                    if (!srcToIndex.TryGetValue(pkSrc, out var pIdx))
                                    {
                                        _logger.LogDebug($"[MERGE] 키 컬럼 '{pkSrc}'이 INSERT 컬럼 목록에 없어 MERGE를 사용할 수 없습니다. 해당 행은 INSERT로 대체합니다.");
                                        executeAsMerge = false;
                                        break;
                                    }
                                    keyPairs.Add((pkTarget, $":p{pIdx}"));
                                }

                                if (executeAsMerge)
                                {
                                    // UPDATE SET 목록 (키 제외)
                                    var keyTargetSet = new HashSet<string>(keyPairs.Select(k => k.Target), StringComparer.OrdinalIgnoreCase);
                                    var updateAssignments = new List<string>();
                                    // mapped 컬럼
                                    for (int i = 0; i < mappedColumns.Count; i++)
                                    {
                                        var tgt = mappedColumns[i].Target;
                                        if (keyTargetSet.Contains(tgt)) continue;
                                        updateAssignments.Add($"t.\"{tgt}\" = :p{i}");
                                    }
                                    // 추가 컬럼 식 (있다면)
                                    if (additionalColumns != null)
                                    {
                                        int ai = 0;
                                        foreach (var addCol in additionalColumns)
                                        {
                                            string expr = ai < (additionalColumnsValues?.Count ?? 0) ? (additionalColumnsValues![ai] ?? "NULL") : "NULL";
                                            var builtExpr = (additionalColumnsValues != null && ai < additionalColumnsValues.Count)
                                                ? BuildAdditionalExpressionForInsert(additionalColumnsValues[ai], mappedColumns.Cast<object>().ToList(), validColumns, row, parameters, ref addParamIndex)
                                                : "NULL";
                                            if (!string.IsNullOrWhiteSpace(addCol))
                                            {
                                                updateAssignments.Add($"t.\"{addCol}\" = {builtExpr}");
                                            }
                                            ai++;
                                        }
                                    }

                                    var onClause = string.Join(" AND ", keyPairs.Select(k => $"t.\"{k.Target}\" = {k.ParamName}"));
                                    var updateClause = updateAssignments.Count > 0 ? "UPDATE SET " + string.Join(", ", updateAssignments) : "UPDATE SET t.\"" + (mappedColumns.First().Target) + "\" = t.\"" + (mappedColumns.First().Target) + "\""; // no-op

                                    sqlToExecute = $"MERGE INTO {tableName} t USING dual ON ({onClause}) WHEN MATCHED THEN {updateClause} WHEN NOT MATCHED THEN INSERT ({columnNames}) VALUES ({parameterNames})";
                                }
                                else
                                {
                                    sqlToExecute = $"INSERT INTO {tableName} ({columnNames}) VALUES ({parameterNames})";
                                }
                            }
                            catch
                            {
                                executeAsMerge = false;
                                sqlToExecute = $"INSERT INTO {tableName} ({columnNames}) VALUES ({parameterNames})";
                            }
                        }
                        else
                        {
                            sqlToExecute = $"INSERT INTO {tableName} ({columnNames}) VALUES ({parameterNames})";
                        }

                        try
                        {
                            using (var command = new OracleCommand(sqlToExecute, oracleConnection))
                            {
                                command.Transaction = (OracleTransaction)transaction;
                                command.CommandTimeout = _commandTimeout;
                                command.BindByName = true;

                                // 파라미터 바인딩
                                foreach (var param in parameters)
                                {
                                    command.Parameters.Add(param);
                                }
                                
                                // 동기 실행
                                int rowsAffected = command.ExecuteNonQuery();
                                
                                if (rowsAffected > 0)
                                {
                                    successCount++;
                                }
                            }

                            // 성공 케이스에서는 임시진단 로깅 생략 (중복 없는 경우 로깅하지 않음)
                        }
                        catch (OracleException oex) when (oex.Number == 1)  // ORA-00001: unique constraint violated
                        {
                            // 중복 키 무시
                            skipCount++;
                            _logger.LogDebug($"중복 행 건너뜀");

                            // 실제 중복행(Oracle 조회 결과가 있는 경우)만 병합 로그 기록
                            try
                            {
                                string? constraintName = null;
                                var msg = oex.Message;
                                var start = msg.IndexOf('(');
                                var end = msg.IndexOf(')');
                                if (start >= 0 && end > start)
                                {
                                    var within = msg.Substring(start + 1, end - start - 1);
                                    var parts = within.Split('.');
                                    constraintName = parts.Length == 2 ? parts[1] : within;
                                }
                                var targetToSourceMap = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
                                foreach (var mc in mappedColumns)
                                {
                                    targetToSourceMap[mc.Target] = mc.Source;
                                }
                                // 현재 실행한 SQL과 파라미터 스냅샷 구성
                                var attemptedParams = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                                foreach (var p in parameters)
                                {
                                    var pname = p.ParameterName;
                                    var pval = p.Value == DBNull.Value ? null : p.Value;
                                    attemptedParams[pname] = pval;
                                }
                                AppendExistingOracleRowsToDuplicateLog(
                                    tableName,
                                    row,
                                    dataTable,
                                    oracleConnection,
                                    (OracleTransaction)transaction,
                                    columnMappings,
                                    primaryKeyColumns,
                                    constraintName,
                                    oraclePkColumns,
                                    targetToSourceMap,
                                    sqlToExecute,
                                    attemptedParams);
                            }
                            catch { }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError($"INSERT 오류: {ex.Message}");
                            throw;
                        }
                    }
                    
                    // 동기 커밋
                    transaction.Commit();
                    
                    _logger.LogInformation($"[InsertIntoOracleSync] 커밋 완료 (성공: {successCount:N0}, 중복: {skipCount:N0})");
                    
                    return (successCount, skipCount);
                }
                catch (Exception ex)
                {
                    _logger.LogError($"[InsertIntoOracleSync] 오류, 롤백 수행: {ex.Message}");
                    transaction.Rollback();
                    throw;
                }
            }
        }
    }

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
                SELECT c.COLUMN_NAME
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
            
            // 실패 시 sys.indexes 사용
            if (pkColumns.Count == 0)
            {
                string query2 = @"
                    SELECT col.name
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
                    
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            pkColumns.Add(reader.GetString(0));
                        }
                    }
                }
            }
            
            return pkColumns;
        }
    }

    /// <summary>
    /// INSERT 문을 구성합니다 (동기 방식용).
    /// </summary>
    #endregion
}

