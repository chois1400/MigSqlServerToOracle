using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlServerToOracleMigrator;

// Load configuration
var configuration = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .Build();

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

var serviceProvider = services.BuildServiceProvider();
var logger = serviceProvider.GetRequiredService<ILogger<Program>>();
var migrationService = serviceProvider.GetRequiredService<MigrationService>();

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

    // Example 2: Migrate specific tables
    // ===== UNCOMMENT AND MODIFY THE FOLLOWING LINES TO ENABLE MIGRATION =====
    
    // Option A: Migrate a single table
    // string sourceTable = "dbo.Employees";
    // string targetTable = "EMPLOYEES";
    // logger.LogInformation($"Starting migration: {sourceTable} -> {targetTable}");
    // await migrationService.MigrateTableAsync(sourceTable, targetTable);
    // logger.LogInformation("✓ Migration completed successfully");

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
    //         // await migrationService.TruncateOracleTableAsync(targetTable);
    //         await migrationService.MigrateTableAsync(sourceTable, targetTable);
    //         logger.LogInformation($"✓ {sourceTable} migrated successfully");
    //     }
    //     catch (Exception ex)
    //     {
    //         logger.LogError($"✗ Failed to migrate {sourceTable}: {ex.Message}");
    //     }
    // }

    // ===== END MIGRATION CODE =====

    logger.LogInformation("========================================");
    logger.LogInformation("To enable table migration:");
    logger.LogInformation("1. Configure 'appsettings.json' with your SQL Server and Oracle connection strings");
    logger.LogInformation("2. Uncomment the migration code above");
    logger.LogInformation("3. Specify source (SQL Server) and target (Oracle) table names");
    logger.LogInformation("4. Run: dotnet run");
    logger.LogInformation("");
    logger.LogInformation("See TESTING_GUIDE.md for detailed instructions.");
}
catch (Exception ex)
{
    logger.LogError($"Unexpected error: {ex.Message}");
    logger.LogError($"Stack trace: {ex.StackTrace}");
    Environment.Exit(1);
}

logger.LogInformation("Program completed");
