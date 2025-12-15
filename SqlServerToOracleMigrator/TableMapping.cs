namespace SqlServerToOracleMigrator;

/// <summary>
/// SQL Server와 Oracle 간의 테이블 매핑 정보
/// </summary>
public class TableMapping
{
    /// <summary>
    /// SQL Server 테이블명 (스키마 포함: dbo.TableName)
    /// </summary>
    public string SqlServerTableName { get; set; } = string.Empty;

    /// <summary>
    /// Oracle 테이블명 (일반적으로 대문자)
    /// </summary>
    public string OracleTableName { get; set; } = string.Empty;

    /// <summary>
    /// 마이그레이션 여부
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// 설명 (선택사항)
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// SQL Server에서 데이터를 추출할 때 사용할 WHERE 절 (예: "IsActive = 1 AND Region = 'KR'")
    /// 빈 값이면 전체 데이터를 추출합니다.
    /// </summary>
    public string? WhereCondition { get; set; }

    /// <summary>
    /// Oracle 대상 테이블의 기존 데이터를 이전 전에 삭제(초기화)할지 여부.
    /// Excel 파일의 6열에서 읽으며 TRUE/YES/1 형태를 허용합니다.
    /// </summary>
    public bool DeleteTarget { get; set; } = false;

    /// <summary>
    /// SQL Server 컬럼명 -> Oracle 컬럼명 매핑 (Key: SQL Server 컬럼명, Value: Oracle 컬럼명)
    /// 테이블의 컬럼명이 다를 경우에 사용됩니다. 빈 경우 1:1 매핑을 가정합니다.
    /// </summary>
    public Dictionary<string, string> ColumnMappings { get; set; } = new();

    /// <summary>
    /// SQL Server의 특정 컬럼에서 빈 문자열(또는 공백)을 '-'로 대체해야 하는 컬럼 목록 (소문자/대문자 무시).
    /// Excel의 I열에 쉼표로 구분하여 지정합니다.
    /// </summary>
    public HashSet<string> EmptyToDashColumns { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// EmptyToDashColumns에서 지정한 컬럼들의 공백값을 대체할 문자열 값.
    /// Excel의 J열에서 읽으며, 기본값은 "-"입니다.
    /// </summary>
    public string EmptyValueReplacement { get; set; } = "-";

    /// <summary>
    /// Oracle 테이블에만 존재하는 추가 컬럼명 목록 (쉼표로 구분).
    /// 예: "CreatedDate,UpdatedDate,IsDeleted"
    /// Excel의 K열에 지정합니다.
    /// </summary>
    public List<string> AdditionalColumns { get; set; } = new();

    /// <summary>
    /// K열의 각 추가 컬럼에 대한 값 또는 함수식 (쉼표로 구분).
    /// - 고정값: '2024-01-01', 'Y', '0'
    /// - 함수: SYSDATE, SYSTIMESTAMP, CURRENT_TIMESTAMP, CURRENT_DATE
    /// - 동적값: {ColumnName} (해당 컬럼값으로 치환)
    /// - 조합: SUBSTR({ColumnName}, 1, 5)
    /// 예: "SYSDATE,SYSTIMESTAMP,{IsActive}"
    /// Excel의 L열에 지정합니다.
    /// </summary>
    public List<string> AdditionalColumnsValues { get; set; } = new();

    /// <summary>
    /// Excel 파일에서 읽은 행 번호 (2부터 시작, 1은 헤더)
    /// </summary>
    public int ExcelRowNumber { get; set; }

    /// <summary>
    /// 마이그레이션 시작 시간
    /// </summary>
    public DateTime? StartTime { get; set; }

    /// <summary>
    /// 마이그레이션 완료 시간
    /// </summary>
    public DateTime? EndTime { get; set; }

    /// <summary>
    /// 마이그레이션 상태 (시작 전, 진행 중, 완료, 실패)
    /// </summary>
    public string Status { get; set; } = "대기";

    /// <summary>
    /// 이전한 레코드 개수 (성공)
    /// </summary>
    public int RecordCount { get; set; } = 0;

    /// <summary>
    /// 중복으로 건너뛴 레코드 개수
    /// </summary>
    public int SkippedCount { get; set; } = 0;

    /// <summary>
    /// 처리 시도한 전체 레코드 개수
    /// </summary>
    public int TotalProcessed { get; set; } = 0;

    /// <summary>
    /// 마이그레이션 실패 사유 (오류 메시지)
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// 배치 처리 시 정렬에 사용할 컬럼명 (쉼표로 구분)
    /// 예: "EQPTID,EIO_CONTROL_STAT_CODE" 
    /// 비어있으면 Primary Key를 자동으로 조회합니다.
    /// Excel의 S열에 지정합니다.
    /// </summary>
    public string? OrderByColumns { get; set; }

    public override string ToString()
    {
        var wherePart = string.IsNullOrWhiteSpace(WhereCondition) ? string.Empty : $" WHERE: {WhereCondition}";
        var truncatePart = DeleteTarget ? " [TRUNCATE_TARGET]" : string.Empty;
        var mappingPart = ColumnMappings.Count > 0 ? $" [COLS: {ColumnMappings.Count}]" : string.Empty;
        return $"{SqlServerTableName} -> {OracleTableName} ({(IsActive ? "활성" : "비활성")}){wherePart}{truncatePart}{mappingPart}";
    }
}
