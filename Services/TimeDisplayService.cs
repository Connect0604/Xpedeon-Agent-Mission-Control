using System.Globalization;
using Microsoft.Extensions.Options;
using XpedeonAgentMissionControl.Configuration;

namespace XpedeonAgentMissionControl.Services;

public class TimeDisplayService
{
    private const string ScheduledRunPrefix = "Scheduled Run";

    public TimeDisplayService(IOptions<TimeDisplayOptions> options)
    {
        TimeZone = ResolveTimeZone(options.Value.TimeZoneId);
    }

    public TimeZoneInfo TimeZone { get; }

    public DateTime ToDisplayTime(DateTime value)
        => TimeZoneInfo.ConvertTimeFromUtc(NormalizeUtc(value), TimeZone);

    public string Format(DateTime value, string format)
        => ToDisplayTime(value).ToString(format, CultureInfo.InvariantCulture);

    public string FormatScheduledRunLabel(DateTime scheduledAtUtc)
        => $"Scheduled Run — {Format(scheduledAtUtc, "MMM d HH:mm")}";

    public string NormalizeScheduledRunLabel(string? label, int? assumedYear = null)
    {
        if (string.IsNullOrWhiteSpace(label))
            return string.Empty;

        if (!label.StartsWith(ScheduledRunPrefix, StringComparison.OrdinalIgnoreCase))
            return label;

        var stamp = label
            .Replace("—", string.Empty, StringComparison.Ordinal)
            .Substring(ScheduledRunPrefix.Length)
            .Trim();

        var year = assumedYear ?? DateTime.UtcNow.Year;
        if (!DateTime.TryParseExact(
                $"{stamp} {year}",
                "MMM d HH:mm yyyy",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out var parsed))
        {
            return label;
        }

        var utc = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
        return FormatScheduledRunLabel(utc);
    }

    private static DateTime NormalizeUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    private static TimeZoneInfo ResolveTimeZone(string? timeZoneId)
    {
        if (!string.IsNullOrWhiteSpace(timeZoneId))
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        return TimeZoneInfo.Local;
    }
}
