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

    public override string ToString()
    {
        var wherePart = string.IsNullOrWhiteSpace(WhereCondition) ? string.Empty : $" WHERE: {WhereCondition}";
        var truncatePart = DeleteTarget ? " [TRUNCATE_TARGET]" : string.Empty;
        var mappingPart = ColumnMappings.Count > 0 ? $" [COLS: {ColumnMappings.Count}]" : string.Empty;
        return $"{SqlServerTableName} -> {OracleTableName} ({(IsActive ? "활성" : "비활성")}){wherePart}{truncatePart}{mappingPart}";
    }
}
