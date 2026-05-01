using Microsoft.EntityFrameworkCore;
using XpedeonAgentMissionControl.Data;
using XpedeonAgentMissionControl.Models;

namespace XpedeonAgentMissionControl.Services;

/// <summary>
/// Background service that polls AgentSchedules every minute and fires
/// tasks for any schedule whose next run time has passed.
/// Uses a simple in-process cron parser; no Quartz job store needed.
/// </summary>
public class AgentSchedulerService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<AgentSchedulerService> _logger;
    private readonly TimeDisplayService _timeDisplay;

    public AgentSchedulerService(
        IServiceProvider services,
        ILogger<AgentSchedulerService> logger,
        TimeDisplayService timeDisplay)
    {
        _services = services;
        _logger = logger;
        _timeDisplay = timeDisplay;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("AgentSchedulerService started.");

        var delay = 60 - DateTime.UtcNow.Second;
        await Task.Delay(TimeSpan.FromSeconds(delay), ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await TickAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AgentSchedulerService tick error.");
            }

            await Task.Delay(TimeSpan.FromMinutes(1), ct);
        }
    }

    private async Task TickAsync()
    {
        await using var scope = _services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        var taskService = scope.ServiceProvider.GetRequiredService<TaskService>();

        await using var db = factory.CreateDbContext();

        var now = DateTime.UtcNow;
        var schedules = await db.AgentSchedules
            .Include(s => s.Agent)
            .Where(s => s.IsEnabled && s.Agent != null)
            .ToListAsync();

        foreach (var schedule in schedules)
        {
            try
            {
                var next = schedule.NextRunAt
                    ?? ComputeNext(schedule.CronExpression, schedule.LastRunAt ?? schedule.CreatedAt, _timeDisplay.TimeZone);

                if (next > now)
                    continue;

                _logger.LogInformation(
                    "Firing scheduled task for agent {AgentId} (schedule {ScheduleId})",
                    schedule.AgentId,
                    schedule.Id);

                await taskService.CreateAndRunAsync(
                    schedule.AgentId,
                    _timeDisplay.FormatScheduledRunLabel(now),
                    schedule.DefaultInput ?? "",
                    schedule.Priority,
                    TriggerSource.Scheduled);

                schedule.LastRunAt = now;
                schedule.TotalRuns++;
                schedule.NextRunAt = ComputeNext(schedule.CronExpression, now, _timeDisplay.TimeZone);
                await db.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fire scheduled task for agent {AgentId}", schedule.AgentId);
            }
        }
    }

    /// <summary>
    /// Parses a 5-field cron expression (min hour dom month dow) and returns
    /// the next UTC DateTime after <paramref name="after"/> using the provided display time zone.
    /// Supports: * (any), */n (every n), n (exact), n-m (range), n,m (list).
    /// </summary>
    public static DateTime ComputeNext(string cron, DateTime after, TimeZoneInfo? timeZone = null)
    {
        var fields = cron.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5)
            return NormalizeUtc(after).AddMinutes(60);

        timeZone ??= TimeZoneInfo.Utc;

        var localAfter = TimeZoneInfo.ConvertTimeFromUtc(NormalizeUtc(after), timeZone);
        var candidate = localAfter.AddMinutes(1);
        candidate = DateTime.SpecifyKind(
            new DateTime(candidate.Year, candidate.Month, candidate.Day, candidate.Hour, candidate.Minute, 0),
            DateTimeKind.Unspecified);

        var limit = candidate.AddYears(1);

        while (candidate < limit)
        {
            if (!Matches(fields[1], candidate.Hour))
            {
                candidate = candidate.AddHours(1).AddMinutes(-candidate.Minute);
                continue;
            }

            if (!Matches(fields[0], candidate.Minute))
            {
                candidate = candidate.AddMinutes(1);
                continue;
            }

            if (!MatchesDayOfMonthAndWeek(fields[2], fields[4], candidate))
            {
                candidate = candidate.AddDays(1).AddHours(-candidate.Hour).AddMinutes(-candidate.Minute);
                continue;
            }

            if (!Matches(fields[3], candidate.Month))
            {
                candidate = candidate.AddMonths(1).AddDays(-(candidate.Day - 1)).AddHours(-candidate.Hour).AddMinutes(-candidate.Minute);
                continue;
            }

            return TimeZoneInfo.ConvertTimeToUtc(candidate, timeZone);
        }

        return NormalizeUtc(after).AddHours(1);
    }

    private static DateTime NormalizeUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    private static bool Matches(string field, int value)
    {
        if (field == "*")
            return true;

        foreach (var part in field.Split(','))
        {
            if (part.StartsWith("*/"))
            {
                if (int.TryParse(part[2..], out var step) && value % step == 0)
                    return true;
            }
            else if (part.Contains('-'))
            {
                var bounds = part.Split('-');
                if (int.TryParse(bounds[0], out var lo) && int.TryParse(bounds[1], out var hi) && value >= lo && value <= hi)
                    return true;
            }
            else if (int.TryParse(part, out var exact) && exact == value)
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesDayOfMonthAndWeek(string dayOfMonthField, string dayOfWeekField, DateTime candidate)
    {
        var domWildcard = dayOfMonthField == "*";
        var dowWildcard = dayOfWeekField == "*";
        var domMatches = Matches(dayOfMonthField, candidate.Day);
        var dowMatches = Matches(dayOfWeekField, (int)candidate.DayOfWeek);

        if (domWildcard && dowWildcard)
            return true;

        if (domWildcard)
            return dowMatches;

        if (dowWildcard)
            return domMatches;

        return domMatches || dowMatches;
    }
}
