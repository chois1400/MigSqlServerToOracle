using ClosedXML.Excel;
using Microsoft.Extensions.Logging;

namespace SqlServerToOracleMigrator;

/// <summary>
/// Excel 파일에서 테이블 매핑 정보를 읽는 서비스
/// </summary>
public class TableMappingReader
{
    private readonly ILogger<TableMappingReader> _logger;

    public TableMappingReader(ILogger<TableMappingReader> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Excel 파일에서 테이블 매핑 정보를 읽습니다.
    /// 
    /// Excel 파일 형식:
    /// - 첫 번째 시트 사용
    /// - A열: SQL Server 테이블명 (예: dbo.Employees)
    /// - B열: Oracle 테이블명 (예: EMPLOYEES)
    /// - C열 (선택): 활성화 여부 (TRUE/FALSE, 기본값: TRUE)
    /// - D열 (선택): 설명
    /// - E열 (선택): WHERE 조건 (예: "IsActive = 1")
    /// - F열 (선택): 대상 Oracle 테이블 초기화 여부 (TRUE/FALSE, 기본값: FALSE)
    /// - G열 (선택): SQL Server 컬럼명 목록 (쉼표로 구분, 예: "EmployeeID,EmployeeName")
    /// - H열 (선택): Oracle 컬럼명 목록 (쉼표로 구분, 예: "EMP_ID,EMP_NAME")
    /// </summary>
    public List<TableMapping> ReadMappingsFromExcel(string filePath)
    {
        var mappings = new List<TableMapping>();

        try
        {
            if (!File.Exists(filePath))
            {
                _logger.LogError($"매핑 파일을 찾을 수 없습니다: {filePath}");
                throw new FileNotFoundException($"매핑 파일을 찾을 수 없습니다: {filePath}");
            }

            using (var workbook = new XLWorkbook(filePath))
            {
                var worksheet = workbook.Worksheets.First();
                _logger.LogInformation($"'{worksheet.Name}' 시트에서 매핑 정보 읽기 중...");

                int rowNumber = 2; // 첫 번째 행은 헤더로 가정
                int processedRows = 0;

                // 데이터 행 반복
                foreach (var row in worksheet.RowsUsed().Skip(1))
                {
                    try
                    {
                        // A열: SQL Server 테이블명
                        var sqlServerTable = row.Cell(1).GetString().Trim();
                        if (string.IsNullOrWhiteSpace(sqlServerTable))
                        {
                            _logger.LogWarning($"행 {rowNumber}: SQL Server 테이블명이 비어있습니다. 건너뜀.");
                            rowNumber++;
                            continue;
                        }

                        // B열: Oracle 테이블명
                        var oracleTable = row.Cell(2).GetString().Trim();
                        if (string.IsNullOrWhiteSpace(oracleTable))
                        {
                            _logger.LogWarning($"행 {rowNumber}: Oracle 테이블명이 비어있습니다. 건너뜀.");
                            rowNumber++;
                            continue;
                        }

                        // C열: 활성화 여부 (기본값: TRUE)
                        bool isActive = true;
                        var activeCell = row.Cell(3);
                        if (!activeCell.IsEmpty())
                        {
                            var activeValue = activeCell.GetString().Trim().ToUpper();
                            isActive = activeValue is "TRUE" or "YES" or "1" or "O" or "활성";
                        }

                        // D열: 설명 (선택사항)
                        var description = row.Cell(4).IsEmpty() ? null : row.Cell(4).GetString().Trim();

                        // E열: WHERE 조건 (선택사항)
                        var whereCondition = row.Cell(5).IsEmpty() ? null : row.Cell(5).GetString().Trim();

                        // F열: 대상 Oracle 테이블 초기화 여부 (선택사항)
                        bool deleteTarget = false;
                        var deleteCell = row.Cell(6);
                        if (!deleteCell.IsEmpty())
                        {
                            var deleteValue = deleteCell.GetString().Trim().ToUpper();
                            deleteTarget = deleteValue is "TRUE" or "YES" or "1" or "O" or "삭제" or "TRUNCATE";
                        }

                        // G열: SQL Server 컬럼명 목록 (쉼표로 구분)
                        // H열: Oracle 컬럼명 목록 (쉼표로 구분)
                        var columnMappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        var sqlServerColumns = row.Cell(7).IsEmpty() ? null : row.Cell(7).GetString().Trim();
                        var oracleColumns = row.Cell(8).IsEmpty() ? null : row.Cell(8).GetString().Trim();

                        if (!string.IsNullOrWhiteSpace(sqlServerColumns) && !string.IsNullOrWhiteSpace(oracleColumns))
                        {
                            var sqlCols = sqlServerColumns.Split(',').Select(c => c.Trim()).ToList();
                            var oracleCols = oracleColumns.Split(',').Select(c => c.Trim()).ToList();

                            if (sqlCols.Count == oracleCols.Count)
                            {
                                for (int i = 0; i < sqlCols.Count; i++)
                                {
                                    if (!string.IsNullOrWhiteSpace(sqlCols[i]) && !string.IsNullOrWhiteSpace(oracleCols[i]))
                                    {
                                        columnMappings[sqlCols[i]] = oracleCols[i];
                                    }
                                }
                            }
                            else
                            {
                                _logger.LogWarning($"행 {rowNumber}: SQL Server 컬럼 개수({sqlCols.Count})와 Oracle 컬럼 개수({oracleCols.Count})가 다릅니다.");
                            }
                        }

                        // I열: 공백값을 대체해야 할 SQL Server 컬럼 목록 (쉼표로 구분)
                        var emptyToDashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        var emptyToDashCell = row.Cell(9).IsEmpty() ? null : row.Cell(9).GetString().Trim();
                        if (!string.IsNullOrWhiteSpace(emptyToDashCell))
                        {
                            var cols = emptyToDashCell.Split(',').Select(c => c.Trim()).Where(c => !string.IsNullOrWhiteSpace(c));
                            foreach (var c in cols)
                                emptyToDashSet.Add(c!);
                        }

                        // J열: 공백값을 대체할 문자열 (기본값: "-")
                        var emptyValueReplacement = "-";
                        var replacementCell = row.Cell(10).IsEmpty() ? null : row.Cell(10).GetString().Trim();
                        if (!string.IsNullOrWhiteSpace(replacementCell))
                        {
                            emptyValueReplacement = replacementCell;
                        }

                        // K열: Oracle 테이블에만 존재하는 추가 컬럼명 (쉼표로 구분)
                        var additionalColumns = new List<string>();
                        var additionalColsCell = row.Cell(11).IsEmpty() ? null : row.Cell(11).GetString().Trim();
                        if (!string.IsNullOrWhiteSpace(additionalColsCell))
                        {
                            var cols = additionalColsCell.Split(',').Select(c => c.Trim()).Where(c => !string.IsNullOrWhiteSpace(c));
                            additionalColumns.AddRange(cols);
                        }

                        // L열: 추가 컬럼의 값 또는 함수식 (쉼표로 구분, K열의 순서와 일치해야 함)
                        var additionalColumnsValues = new List<string>();
                        var additionalValuesCell = row.Cell(12).IsEmpty() ? null : row.Cell(12).GetString().Trim();
                        if (!string.IsNullOrWhiteSpace(additionalValuesCell))
                        {
                            // 함수식을 정확히 파싱: 괄호 내의 쉼표는 무시하고, 괄호 밖의 쉼표로만 split
                            // 예: "TO_CHAR({INSDTTM}, 'YYYYMMDDHHMMSSFF9'),SYSDATE,'ACTIVE'"
                            // → ["TO_CHAR({INSDTTM}, 'YYYYMMDDHHMMSSFF9')", "SYSDATE", "'ACTIVE'"]
                            var values = SplitPreservingParentheses(additionalValuesCell);
                            additionalColumnsValues.AddRange(values);
                            
                            _logger.LogInformation($"행 {rowNumber}: L열 파싱 결과 = [{string.Join(", ", values.Select(v => $"\"{v}\""))}]");
                        }

                        // K와 L 열의 개수가 일치하는지 확인
                        if (additionalColumns.Count > 0 && additionalColumnsValues.Count > 0 && additionalColumns.Count != additionalColumnsValues.Count)
                        {
                            _logger.LogWarning($"행 {rowNumber}: 추가 컬럼 개수({additionalColumns.Count})와 값 개수({additionalColumnsValues.Count})가 다릅니다. 개수가 적은 만큼만 적용됩니다.");
                        }

                        var mapping = new TableMapping
                        {
                            SqlServerTableName = sqlServerTable,
                            OracleTableName = oracleTable,
                            IsActive = isActive,
                            Description = description,
                            WhereCondition = whereCondition,
                            DeleteTarget = deleteTarget,
                            ColumnMappings = columnMappings,
                            EmptyToDashColumns = emptyToDashSet,
                            EmptyValueReplacement = emptyValueReplacement,
                            AdditionalColumns = additionalColumns,
                            AdditionalColumnsValues = additionalColumnsValues,
                            ExcelRowNumber = rowNumber
                        };

                        mappings.Add(mapping);
                        processedRows++;
                        _logger.LogDebug($"행 {rowNumber}: {mapping}");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning($"행 {rowNumber}을를 처리하는 중 오류 발생: {ex.Message}. 건너뜀.");
                    }

                    rowNumber++;
                }

                _logger.LogInformation($"총 {processedRows}개의 매핑 정보를 읽었습니다.");
                _logger.LogInformation($"활성 매핑: {mappings.Count(m => m.IsActive)}개");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"Excel 파일 읽기 중 오류 발생: {ex.Message}");
            throw;
        }

        return mappings;
    }

    /// <summary>
    /// 샘플 Excel 매핑 파일을 생성합니다.
    /// </summary>
    public void CreateSampleMappingFile(string filePath)
    {
        try
        {
            using (var workbook = new XLWorkbook())
            {
                var worksheet = workbook.Worksheets.Add("TableMapping");

                // 헤더 행
                worksheet.Cell(1, 1).Value = "SQL Server 테이블명";
                worksheet.Cell(1, 2).Value = "Oracle 테이블명";
                worksheet.Cell(1, 3).Value = "활성화";
                worksheet.Cell(1, 4).Value = "설명";
                worksheet.Cell(1, 5).Value = "WhereCondition";
                worksheet.Cell(1, 6).Value = "TruncateTarget";
                worksheet.Cell(1, 7).Value = "SQL Server 컬럼명";
                worksheet.Cell(1, 8).Value = "Oracle 컬럼명";
                worksheet.Cell(1, 9).Value = "EmptyToDashColumns (SQL 컬럼명, 쉼표 구분)";
                worksheet.Cell(1, 10).Value = "EmptyReplacement (예: - or 'N/A')";
                worksheet.Cell(1, 11).Value = "AdditionalColumns (Oracle 전용 컬럼명, 쉼표 구분)";
                worksheet.Cell(1, 12).Value = "AdditionalColumnsValues (값 또는 함수, 쉼표 구분)";

                // 헤더 스타일
                var headerRow = worksheet.Row(1);
                headerRow.Style.Font.Bold = true;
                headerRow.Style.Fill.BackgroundColor = XLColor.LightGray;
                headerRow.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

                // 샘플 데이터
                worksheet.Cell(2, 1).Value = "dbo.Employees";
                worksheet.Cell(2, 2).Value = "EMPLOYEES";
                worksheet.Cell(2, 3).Value = "TRUE";
                worksheet.Cell(2, 4).Value = "직원 정보 테이블 (TO_CHAR 함수 예제)";
                worksheet.Cell(2, 5).Value = "IsActive = 1";
                worksheet.Cell(2, 6).Value = "TRUE";
                worksheet.Cell(2, 7).Value = "EmployeeID,EmployeeName,INSDTTM";
                worksheet.Cell(2, 8).Value = "EMP_ID,EMP_NAME,INS_DT";
                worksheet.Cell(2, 9).Value = "EmployeeName";
                worksheet.Cell(2, 10).Value = "-";
                worksheet.Cell(2, 11).Value = "FormattedInsDate,CreatedDate,IsDeleted,Department";
                worksheet.Cell(2, 12).Value = "TO_CHAR({INSDTTM}, 'YYYYMMDDHHMMSSFF9'),SYSDATE,'N','HR'";

                worksheet.Cell(3, 1).Value = "dbo.Departments";
                worksheet.Cell(3, 2).Value = "DEPARTMENTS";
                worksheet.Cell(3, 3).Value = "TRUE";
                worksheet.Cell(3, 4).Value = "부서 정보 테이블";
                worksheet.Cell(3, 5).Value = "";
                worksheet.Cell(3, 6).Value = "FALSE";
                worksheet.Cell(3, 7).Value = "";
                worksheet.Cell(3, 8).Value = "";
                worksheet.Cell(3, 9).Value = "";
                worksheet.Cell(3, 10).Value = "";
                worksheet.Cell(3, 11).Value = "UpdatedDate";
                worksheet.Cell(3, 12).Value = "SYSTIMESTAMP";

                worksheet.Cell(4, 1).Value = "dbo.Projects";
                worksheet.Cell(4, 2).Value = "PROJECTS";
                worksheet.Cell(4, 3).Value = "FALSE";
                worksheet.Cell(4, 4).Value = "현재는 마이그레이션 제외";
                worksheet.Cell(4, 5).Value = "Status = 'Completed'";
                worksheet.Cell(4, 6).Value = "FALSE";
                worksheet.Cell(4, 7).Value = "";
                worksheet.Cell(4, 8).Value = "";
                worksheet.Cell(4, 9).Value = "";
                worksheet.Cell(4, 10).Value = "";
                worksheet.Cell(4, 11).Value = "MigrationDate,MigratedFrom";
                worksheet.Cell(4, 12).Value = "CURRENT_DATE,'SQL_Server'";

                // 컬럼 너비 조정
                worksheet.Column(1).Width = 25;
                worksheet.Column(2).Width = 25;
                worksheet.Column(3).Width = 12;
                worksheet.Column(4).Width = 30;
                worksheet.Column(5).Width = 40;
                worksheet.Column(6).Width = 16;
                worksheet.Column(7).Width = 35;
                worksheet.Column(8).Width = 35;
                worksheet.Column(9).Width = 35;
                worksheet.Column(10).Width = 20;
                worksheet.Column(11).Width = 40;
                worksheet.Column(12).Width = 40;

                workbook.SaveAs(filePath);
                _logger.LogInformation($"샘플 매핑 파일이 생성되었습니다: {filePath}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"샘플 매핑 파일 생성 중 오류 발생: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// 괄호를 고려하여 쉼표로 분리합니다.
    /// 예: "TO_CHAR({Col}, 'fmt'),SYSDATE,'Y'" → ["TO_CHAR({Col}, 'fmt')", "SYSDATE", "'Y'"]
    /// </summary>
    private List<string> SplitPreservingParentheses(string input)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        int parenDepth = 0;
        bool inSingleQuote = false;
        bool inDoubleQuote = false;

        foreach (var ch in input)
        {
            // 따옴표 처리
            if (ch == '\'' && !inDoubleQuote)
                inSingleQuote = !inSingleQuote;
            else if (ch == '"' && !inSingleQuote)
                inDoubleQuote = !inDoubleQuote;

            // 괄호 깊이 추적 (따옴표 밖에서만)
            if (!inSingleQuote && !inDoubleQuote)
            {
                if (ch == '(')
                    parenDepth++;
                else if (ch == ')')
                    parenDepth--;
            }

            // 쉼표 처리 (괄호 밖에서만 분리)
            if (ch == ',' && parenDepth == 0 && !inSingleQuote && !inDoubleQuote)
            {
                var value = current.ToString().Trim();
                if (!string.IsNullOrWhiteSpace(value))
                    result.Add(value);
                current.Clear();
            }
            else
            {
                current.Append(ch);
            }
        }

        // 마지막 항목 추가
        var lastValue = current.ToString().Trim();
        if (!string.IsNullOrWhiteSpace(lastValue))
            result.Add(lastValue);

        return result;
    }

    /// <summary>
    /// Excel 파일에 마이그레이션 시간 정보를 기록합니다.
    /// M열: 시작 시간, N열: 완료 시간, O열: 상태, P열: 소요 시간, Q열: 이전 레코드 개수
    /// </summary>
    public void UpdateMigrationTimes(string filePath, List<TableMapping> mappings)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                _logger.LogError($"매핑 파일을 찾을 수 없습니다: {filePath}");
                return;
            }

            // 임시 파일에 작업한 후 원본 파일로 덮어쓰기
            string tempPath = Path.GetTempFileName();
            
            try
            {
                using (var workbook = new XLWorkbook(filePath))
                {
                    var worksheet = workbook.Worksheets.First();
                    
                    // 헤더 설정 (M, N, O, P, Q 열)
                    if (worksheet.Cell(1, 13).IsEmpty())
                    {
                        worksheet.Cell(1, 13).Value = "시작 시간";
                        worksheet.Cell(1, 14).Value = "완료 시간";
                        worksheet.Cell(1, 15).Value = "상태";
                        worksheet.Cell(1, 16).Value = "소요 시간";
                        worksheet.Cell(1, 17).Value = "이전 레코드 수";
                        worksheet.Cell(1, 13).Style.Font.Bold = true;
                        worksheet.Cell(1, 14).Style.Font.Bold = true;
                        worksheet.Cell(1, 15).Style.Font.Bold = true;
                        worksheet.Cell(1, 16).Style.Font.Bold = true;
                        worksheet.Cell(1, 17).Style.Font.Bold = true;
                    }

                    foreach (var mapping in mappings)
                    {
                        if (mapping.ExcelRowNumber > 0)
                        {
                            int row = mapping.ExcelRowNumber;
                            
                            // M열: 시작 시간
                            if (mapping.StartTime.HasValue)
                            {
                                worksheet.Cell(row, 13).Value = mapping.StartTime.Value.ToString("yyyy-MM-dd HH:mm:ss");
                            }
                            
                            // N열: 완료 시간
                            if (mapping.EndTime.HasValue)
                            {
                                worksheet.Cell(row, 14).Value = mapping.EndTime.Value.ToString("yyyy-MM-dd HH:mm:ss");
                                
                                // 소요 시간 계산
                                if (mapping.StartTime.HasValue)
                                {
                                    var duration = mapping.EndTime.Value - mapping.StartTime.Value;
                                    worksheet.Cell(row, 16).Value = $"{duration.TotalSeconds:F2}초";
                                }
                            }
                            
                            // O열: 상태
                            worksheet.Cell(row, 15).Value = mapping.Status;
                            
                            // Q열: 이전 레코드 개수
                            if (mapping.RecordCount > 0)
                            {
                                worksheet.Cell(row, 17).Value = mapping.RecordCount;
                            }
                            
                            // 상태에 따라 색상 지정
                            var statusCell = worksheet.Cell(row, 15);
                            switch (mapping.Status)
                            {
                                case "완료":
                                    statusCell.Style.Fill.BackgroundColor = XLColor.LightGreen;
                                    break;
                                case "실패":
                                    statusCell.Style.Fill.BackgroundColor = XLColor.LightPink;
                                    break;
                                case "진행 중":
                                    statusCell.Style.Fill.BackgroundColor = XLColor.LightYellow;
                                    break;
                            }
                        }
                    }

                    // 임시 파일에 저장
                    workbook.SaveAs(tempPath);
                }

                // 원본 파일 삭제 후 임시 파일 이름 변경
                File.Delete(filePath);
                File.Move(tempPath, filePath);
                
                _logger.LogInformation($"마이그레이션 시간 정보를 Excel 파일에 기록했습니다: {filePath}");
            }
            catch
            {
                // 임시 파일 정리
                if (File.Exists(tempPath))
                {
                    try { File.Delete(tempPath); } catch { }
                }
                throw;
            }
        }
        catch
        {
            _logger.LogError($"Excel 파일 업데이트 중 오류 발생");
        }
    }
}