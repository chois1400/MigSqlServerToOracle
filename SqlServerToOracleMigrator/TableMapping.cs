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

    public override string ToString()
    {
        return $"{SqlServerTableName} -> {OracleTableName} ({(IsActive ? "활성" : "비활성")})";
    }
}
