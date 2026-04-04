namespace XpedeonAgentMissionControl.Configuration;

public class HermesOpenClawConfig
{
    public bool Enabled { get; set; } = false;
    public string BaseUrl { get; set; } = string.Empty;
    public string SubmitPath { get; set; } = "/api/runs";
    public string StatusPathTemplate { get; set; } = "/api/runs/{runId}";
    public string CancelPathTemplate { get; set; } = "/api/runs/{runId}/cancel";
    public string ApiKey { get; set; } = string.Empty;
    public int PollIntervalSeconds { get; set; } = 5;
    public int SyncBatchSize { get; set; } = 20;
}
