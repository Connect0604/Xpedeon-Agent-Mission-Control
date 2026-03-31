namespace XpedeonAgentMissionControl.Models;

public enum FeedbackType { ThumbsUp, ThumbsDown, Rating, Correction }

public class TaskFeedback
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string TaskId { get; set; } = string.Empty;
    public string AgentId { get; set; } = string.Empty;
    public FeedbackType Type { get; set; }
    public int Rating { get; set; }              // 1–5
    public string? Note { get; set; }
    public string? CorrectedOutput { get; set; } // human correction
    public bool UsedForLearning { get; set; } = false;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
