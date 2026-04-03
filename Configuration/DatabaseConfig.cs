namespace XpedeonAgentMissionControl.Configuration;

public class DatabaseConfig
{
    public string Provider { get; set; } = "SQLite";
    public string Server { get; set; } = string.Empty;
    public string Database { get; set; } = "BZMGTLDB";
    public string Schema { get; set; } = "amc";
    public bool IntegratedSecurity { get; set; } = true;
    public string UserId { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string FilePath { get; set; } = "xpedeon.db";
    public bool TrustServerCertificate { get; set; } = true;
}
