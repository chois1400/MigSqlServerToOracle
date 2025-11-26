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
                            EmptyValueReplacement = emptyValueReplacement
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

                // 헤더 스타일
                var headerRow = worksheet.Row(1);
                headerRow.Style.Font.Bold = true;
                headerRow.Style.Fill.BackgroundColor = XLColor.LightGray;
                headerRow.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

                // 샘플 데이터
                worksheet.Cell(2, 1).Value = "dbo.Employees";
                worksheet.Cell(2, 2).Value = "EMPLOYEES";
                worksheet.Cell(2, 3).Value = "TRUE";
                worksheet.Cell(2, 4).Value = "직원 정보 테이블";
                worksheet.Cell(2, 5).Value = "IsActive = 1";
                worksheet.Cell(2, 6).Value = "TRUE";
                worksheet.Cell(2, 7).Value = "EmployeeID,EmployeeName";
                worksheet.Cell(2, 8).Value = "EMP_ID,EMP_NAME";
                worksheet.Cell(2, 9).Value = "EmployeeName";
                worksheet.Cell(2, 10).Value = "-";

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
}
