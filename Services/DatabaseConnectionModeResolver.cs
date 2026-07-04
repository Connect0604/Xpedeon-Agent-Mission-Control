using Microsoft.Data.SqlClient;
using XpedeonAgentMissionControl.Configuration;

namespace XpedeonAgentMissionControl.Services;

public static class DatabaseConnectionModeResolver
{
    public static ResolvedDatabaseMode Resolve(
        DatabaseConfig config,
        string contentRootPath,
        Func<string, bool>? sqlServerConnectivityProbe = null)
    {
        var provider = (config.Provider ?? "SQLite").Trim();
        if (!provider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase))
        {
            return ResolveSqlite(config, contentRootPath, null);
        }

        var sqlServerConnectionString = BuildSqlServerConnectionString(config);
        var probe = sqlServerConnectivityProbe ?? CanConnectToSqlServer;
        if (!config.FallbackToSqliteWhenSqlServerUnavailable || probe(sqlServerConnectionString))
        {
            return new ResolvedDatabaseMode("SqlServer", sqlServerConnectionString, null);
        }

        var message = $"Configured SQL Server '{config.Server}' is unavailable; falling back to SQLite.";
        return ResolveSqlite(config, contentRootPath, message);
    }

    public static string BuildSqlServerConnectionString(DatabaseConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.Server))
            throw new InvalidOperationException("Database server is required for SqlServer provider.");

        if (string.IsNullOrWhiteSpace(config.Database))
            throw new InvalidOperationException("Database name is required for SqlServer provider.");

        var parts = new List<string>
        {
            $"Server={config.Server}",
            $"Database={config.Database}",
            $"TrustServerCertificate={(config.TrustServerCertificate ? "True" : "False")}",
            "Connect Timeout=5"
        };

        if (config.IntegratedSecurity)
        {
            parts.Add("Integrated Security=True");
        }
        else
        {
            parts.Add($"User Id={config.UserId}");
            parts.Add($"Password={config.Password}");
        }

        return string.Join(";", parts);
    }

    private static ResolvedDatabaseMode ResolveSqlite(DatabaseConfig config, string contentRootPath, string? startupMessage)
    {
        var sqlitePath = config.FilePath;
        if (string.IsNullOrWhiteSpace(sqlitePath))
            sqlitePath = "xpedeon.db";

        if (!Path.IsPathRooted(sqlitePath))
            sqlitePath = Path.Combine(contentRootPath, sqlitePath);

        return new ResolvedDatabaseMode("SQLite", $"Data Source={sqlitePath}", startupMessage);
    }

    private static bool CanConnectToSqlServer(string connectionString)
    {
        try
        {
            using var connection = new SqlConnection(connectionString);
            connection.Open();
            return true;
        }
        catch
        {
            return false;
        }
    }
}

public sealed record ResolvedDatabaseMode(
    string Provider,
    string ConnectionString,
    string? StartupMessage);
