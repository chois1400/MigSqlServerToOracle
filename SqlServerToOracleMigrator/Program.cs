using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlServerToOracleMigrator;

// Parse command-line arguments for optional config and mapping file paths
string? GetArgValue(string name)
{
    for (int i = 0; i < args.Length; i++)
    {
        var a = args[i];
        if (a.StartsWith($"--{name}=") || a.StartsWith($"-{name}="))
            return a.Substring(a.IndexOf('=') + 1).Trim('"');

        if (a == $"--{name}" || a == $"-{name[0]}")
            return i + 1 < args.Length ? args[i + 1].Trim('"') : null;
    }
    return null;
}

var configArg = GetArgValue("config") ?? GetArgValue("c");
var mappingArg = GetArgValue("mapping") ?? GetArgValue("m");
var createSampleFlag = args.Contains("--create-sample") || args.Contains("-s");

// If config not specified, ask user whether to continue with default 'appsettings.json'
if (string.IsNullOrWhiteSpace(configArg))
{
    Console.Write("appsettings.json 경로를 지정하지 않았습니다. 기본 'appsettings.json'을 사용하여 계속 진행하시겠습니까? (Y/N): ");
    var resp = Console.ReadLine()?.Trim().ToUpperInvariant();
    if (resp != "Y" && resp != "YES")
    {
        Console.WriteLine("작업을 종료합니다.");
        return;
    }
}

// Build configuration (use provided path or default)
var configBuilder = new ConfigurationBuilder();
if (!string.IsNullOrWhiteSpace(configArg))
{
    var cfgPath = Path.IsPathRooted(configArg) ? configArg : Path.Combine(Directory.GetCurrentDirectory(), configArg);
    if (!File.Exists(cfgPath))
    {
        Console.WriteLine($"지정된 설정 파일을 찾을 수 없습니다: {cfgPath}");
        return;
    }
    configBuilder.AddJsonFile(cfgPath, optional: false, reloadOnChange: true);
}
else
{
    configBuilder.SetBasePath(Directory.GetCurrentDirectory())
                 .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
}

var configuration = configBuilder.Build();

// Setup dependency injection
var services = new ServiceCollection();
services.AddLogging(builder =>
{
    builder.AddConsole();
    builder.SetMinimumLevel(LogLevel.Information);
});

services.AddSingleton<MigrationService>(sp =>
{
    var sqlServerConnStr = configuration.GetConnectionString("SqlServer") 
        ?? throw new InvalidOperationException("SqlServer connection string not found");
    var oracleConnStr = configuration.GetConnectionString("Oracle") 
        ?? throw new InvalidOperationException("Oracle connection string not found");
    
    var batchSize = configuration.GetValue<int>("MigrationSettings:BatchSize", 1000);
    var commandTimeout = configuration.GetValue<int>("MigrationSettings:CommandTimeout", 300);
    var logger = sp.GetRequiredService<ILogger<MigrationService>>();

    return new MigrationService(sqlServerConnStr, oracleConnStr, batchSize, commandTimeout, logger);
});

services.AddSingleton<TableMappingReader>();

var serviceProvider = services.BuildServiceProvider();
var logger = serviceProvider.GetRequiredService<ILogger<Program>>();
var migrationService = serviceProvider.GetRequiredService<MigrationService>();
var mappingReader = serviceProvider.GetRequiredService<TableMappingReader>();

// If --create-sample / -s flag present, create sample mapping file and exit
if (createSampleFlag)
{
    var samplePath = Path.Combine(Directory.GetCurrentDirectory(), mappingArg ?? "TableMapping.xlsx");
    mappingReader.CreateSampleMappingFile(samplePath);
    logger.LogInformation($"샘플 파일 생성 완료: {samplePath}");
    return;
}

try
{
    logger.LogInformation("SQL Server to Oracle Migration Tool Started");
    logger.LogInformation("========================================");

    // Example 1: Get all source tables
    logger.LogInformation("Retrieving source tables from SQL Server...");
    try
    {
        var tables = await migrationService.GetSourceTablesAsync();
        logger.LogInformation($"Found {tables.Count} tables: {string.Join(", ", tables)}");
        
        if (tables.Count == 0)
        {
            logger.LogWarning("No tables found in SQL Server database.");
        }
    }
    catch (Exception ex)
    {
        logger.LogError($"Could not retrieve tables: {ex.Message}");
        logger.LogInformation("Verify your SQL Server connection string in appsettings.json");
        Environment.Exit(1);
    }

    // Example 2: Migrate tables using Excel mapping (active flow)
    // If a mapping file path was provided via --mapping/-m, use it. Otherwise ask user whether to proceed with default 'TableMapping.xlsx'.
    try
    {
        string? mappingFileToUse = null;

        if (!string.IsNullOrWhiteSpace(mappingArg))
        {
            mappingFileToUse = Path.IsPathRooted(mappingArg) ? mappingArg : Path.Combine(Directory.GetCurrentDirectory(), mappingArg);
            if (!File.Exists(mappingFileToUse))
            {
                logger.LogError($"지정된 매핑 파일을 찾을 수 없습니다: {mappingFileToUse}");
                mappingFileToUse = null;
            }
        }

        if (string.IsNullOrWhiteSpace(mappingFileToUse))
        {
            Console.Write("TableMapping.xlsx 경로를 지정하지 않았습니다. 기본 'TableMapping.xlsx'을 사용하여 매핑 기반 마이그레이션을 실행하시겠습니까? (Y/N): ");
            var ans = Console.ReadLine()?.Trim().ToUpperInvariant();
            if (ans == "Y" || ans == "YES")
            {
                var defaultPath = Path.Combine(Directory.GetCurrentDirectory(), "TableMapping.xlsx");
                if (File.Exists(defaultPath))
                {
                    mappingFileToUse = defaultPath;
                }
                else
                {
                    Console.Write("기본 매핑 파일을 찾을 수 없습니다. 샘플 매핑 파일을 생성하시겠습니까? (Y/N): ");
                    var createAns = Console.ReadLine()?.Trim().ToUpperInvariant();
                    if (createAns == "Y" || createAns == "YES")
                    {
                        var samplePath = Path.Combine(Directory.GetCurrentDirectory(), "TableMapping.xlsx");
                        mappingReader.CreateSampleMappingFile(samplePath);
                        logger.LogInformation($"샘플 매핑 파일 생성됨: {samplePath}");
                        mappingFileToUse = samplePath;
                    }
                    else
                    {
                        logger.LogInformation("매핑 기반 마이그레이션을 건너뜁니다.");
                    }
                }
            }
            else
            {
                logger.LogInformation("매핑 기반 마이그레이션을 건너뜁니다.");
            }
        }

        if (!string.IsNullOrWhiteSpace(mappingFileToUse))
        {
            logger.LogInformation($"Excel 매핑 파일 사용: {mappingFileToUse}");
            var mappings = mappingReader.ReadMappingsFromExcel(mappingFileToUse);
            logger.LogInformation($"읽은 매핑 정보: {mappings.Count}개");
            if (mappings.Any())
            {
                await migrationService.MigrateWithMappingAsync(mappings);
            }
            else
            {
                logger.LogWarning("매핑 정보가 비어있습니다. 매핑 파일을 확인하세요.");
            }
        }
    }
    catch (Exception ex)
    {
        logger.LogError($"Excel 매핑 처리 중 오류: {ex.Message}");
    }

    // Example 3: Migrate specific tables manually
    // ===== OPTION B: 수동으로 특정 테이블 마이그레이션 =====
    
    // Migrate a single table
    // string sourceTable = "dbo.Employees";
    // string targetTable = "EMPLOYEES";
    // logger.LogInformation($"Starting migration: {sourceTable} -> {targetTable}");
    // await migrationService.MigrateTableAsync(sourceTable, targetTable);
    // logger.LogInformation("✓ Migration completed successfully");

    // Migrate multiple tables
    // var tablesToMigrate = new[]
    // {
    //     ("dbo.Employees", "EMPLOYEES"),
    //     ("dbo.Departments", "DEPARTMENTS"),
    //     ("dbo.Projects", "PROJECTS")
    // };
    // 
    // foreach (var (sourceTable, targetTable) in tablesToMigrate)
    // {
    //     try
    //     {
    //         logger.LogInformation($"Migrating: {sourceTable} -> {targetTable}");
    //         // Optionally truncate existing data:
    //         // await migrationService.DeleteOracleTableAsync(targetTable);
    //         await migrationService.MigrateTableAsync(sourceTable, targetTable);
    //         logger.LogInformation($"✓ {sourceTable} migrated successfully");
    //     }
    //     catch (Exception ex)
    //     {
    //         logger.LogError($"✗ Failed to migrate {sourceTable}: {ex.Message}");
    //     }
    // }

    // ===== END MIGRATION CODE =====

    // Option B: Migrate multiple tables
    // var tablesToMigrate = new[]
    // {
    //     ("dbo.Employees", "EMPLOYEES"),
    //     ("dbo.Departments", "DEPARTMENTS"),
    //     ("dbo.Projects", "PROJECTS")
    // };
    // 
    // foreach (var (sourceTable, targetTable) in tablesToMigrate)
    // {
    //     try
    //     {
    //         logger.LogInformation($"Migrating: {sourceTable} -> {targetTable}");
    //         // Optionally truncate existing data:
    //         // await migrationService.DeleteOracleTableAsync(targetTable);
    //         await migrationService.MigrateTableAsync(sourceTable, targetTable);
    //         logger.LogInformation($"✓ {sourceTable} migrated successfully");
    //     }
    //     catch (Exception ex)
    //     {
    //         logger.LogError($"✗ Failed to migrate {sourceTable}: {ex.Message}");
    //     }
    // }

    // ===== END MIGRATION CODE =====

    // Example 3: Excel 매핑 파일 생성 및 사용
    // ===== UNCOMMENT THE FOLLOWING LINES TO ENABLE EXCEL MAPPING =====
    
    // 1단계: 샘플 Excel 매핑 파일 생성
    // string mappingFilePath = Path.Combine(Directory.GetCurrentDirectory(), "TableMapping.xlsx");
    // logger.LogInformation("샘플 Excel 매핑 파일 생성 중...");
    // mappingReader.CreateSampleMappingFile(mappingFilePath);
    // logger.LogInformation($"✓ 샘플 매핑 파일 생성됨: {mappingFilePath}");
    // logger.LogInformation("");
    // logger.LogInformation("1. Excel 파일을 편집하여 마이그레이션할 테이블을 지정하세요.");
    // logger.LogInformation("   - A열: SQL Server 테이블명 (예: dbo.Employees)");
    // logger.LogInformation("   - B열: Oracle 테이블명 (예: EMPLOYEES)");
    // logger.LogInformation("   - C열: 활성화 여부 (TRUE/FALSE, 기본값: TRUE)");
    // logger.LogInformation("   - D열: 설명 (선택사항)");
    // logger.LogInformation("");

    // 2단계: Excel 파일에서 매핑 읽기
    // string mappingFile = "TableMapping.xlsx";  // Excel 파일 경로 지정
    // if (File.Exists(mappingFile))
    // {
    //     try
    //     {
    //         logger.LogInformation($"Excel 매핑 파일 읽기: {mappingFile}");
    //         var mappings = mappingReader.ReadMappingsFromExcel(mappingFile);
    //         logger.LogInformation($"읽은 매핑 정보: {mappings.Count}개");
    //         foreach (var mapping in mappings)
    //         {
    //             logger.LogInformation($"  - {mapping}");
    //         }
    //         logger.LogInformation("");
    //
    //         // 3단계: 매핑에 따라 마이그레이션 수행
    //         logger.LogInformation("Excel 매핑을 기반으로 마이그레이션 시작...");
    //         await migrationService.MigrateWithMappingAsync(mappings);
    //     }
    //     catch (Exception ex)
    //     {
    //         logger.LogError($"Excel 매핑 처리 중 오류: {ex.Message}");
    //     }
    // }
    // else
    // {
    //     logger.LogWarning($"Excel 파일을 찾을 수 없습니다: {mappingFile}");
    //     logger.LogInformation("사용법:");
    //     logger.LogInformation("1. 샘플 파일을 생성하려면 위의 CreateSampleMappingFile 코드를 활성화하세요.");
    //     logger.LogInformation("2. 생성된 TableMapping.xlsx를 편집하여 테이블 매핑을 입력하세요.");
    //     logger.LogInformation("3. 위의 마이그레이션 코드를 활성화하세요.");
    // }

    // ===== END EXCEL MAPPING CODE =====

    logger.LogInformation("========================================");
    logger.LogInformation("마이그레이션 실행 옵션:");
    logger.LogInformation("");
    logger.LogInformation("옵션 1: 수동으로 테이블 지정");
    logger.LogInformation("  - Program.cs의 '옵션 A' 또는 '옵션 B' 섹션 주석 해제");
    logger.LogInformation("  - 소스/대상 테이블명 지정 후 실행");
    logger.LogInformation("");
    logger.LogInformation("옵션 2: Excel 매핑 파일 사용 (권장)");
    logger.LogInformation("  - Program.cs의 '예제 3' 섹션 주석 해제");
    logger.LogInformation("  - 샘플 Excel 파일 생성 후 편집");
    logger.LogInformation("  - 다시 실행하여 매핑된 테이블만 마이그레이션");
    logger.LogInformation("");
    logger.LogInformation("자세한 사항은 README.md 또는 TESTING_GUIDE.md 참고하세요.");
}
catch (Exception ex)
{
    logger.LogError($"Unexpected error: {ex.Message}");
    logger.LogError($"Stack trace: {ex.StackTrace}");
    Environment.Exit(1);
}

logger.LogInformation("Program completed");
