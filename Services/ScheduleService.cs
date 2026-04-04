using Microsoft.EntityFrameworkCore;
using XpedeonAgentMissionControl.Data;
using XpedeonAgentMissionControl.Models;

namespace XpedeonAgentMissionControl.Services;

public class ScheduleService
{
    private readonly IDbContextFactory<AppDbContext> _factory;

    public ScheduleService(IDbContextFactory<AppDbContext> factory)
    {
        _factory = factory;
    }

    public async Task<List<AgentSchedule>> GetSchedulesAsync()
    {
        await using var db = _factory.CreateDbContext();
        return await db.AgentSchedules
            .Include(s => s.Agent)
            .OrderByDescending(s => s.IsEnabled)
            .ThenBy(s => s.NextRunAt ?? DateTime.MaxValue)
            .ToListAsync();
    }

    public async Task<List<AgentTask>> GetRecentScheduledTasksAsync(int count = 50)
    {
        await using var db = _factory.CreateDbContext();
        return await db.Tasks
            .Where(t => t.TriggerSource == TriggerSource.Scheduled)
            .OrderByDescending(t => t.CreatedAt)
            .Take(count)
            .ToListAsync();
    }

    public async Task<ScheduleOverview> GetOverviewAsync()
    {
        await using var db = _factory.CreateDbContext();
        var now = DateTime.UtcNow;
        var schedules = await db.AgentSchedules.ToListAsync();
        var recentScheduledTasks = await db.Tasks
            .Where(t => t.TriggerSource == TriggerSource.Scheduled && t.CreatedAt >= now.Date)
            .ToListAsync();

        return new ScheduleOverview
        {
            TotalSchedules = schedules.Count,
            EnabledSchedules = schedules.Count(s => s.IsEnabled),
            NextHourSchedules = schedules.Count(s => s.IsEnabled && s.NextRunAt.HasValue && s.NextRunAt.Value <= now.AddHours(1)),
            OverdueSchedules = schedules.Count(s => s.IsEnabled && s.NextRunAt.HasValue && s.NextRunAt.Value < now),
            ScheduledTasksToday = recentScheduledTasks.Count,
            ScheduledFailuresToday = recentScheduledTasks.Count(t => t.Status == AgentTaskStatus.Failed)
        };
    }
}

public class ScheduleOverview
{
    public int TotalSchedules { get; set; }
    public int EnabledSchedules { get; set; }
    public int NextHourSchedules { get; set; }
    public int OverdueSchedules { get; set; }
    public int ScheduledTasksToday { get; set; }
    public int ScheduledFailuresToday { get; set; }
}
