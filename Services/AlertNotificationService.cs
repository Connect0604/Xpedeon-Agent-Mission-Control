using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using XpedeonAgentMissionControl.Data;
using XpedeonAgentMissionControl.Models;

namespace XpedeonAgentMissionControl.Services;

public interface IAlertNotificationHandler
{
    Task<bool> SendAsync(string recipient, string message);
}

public class EmailAlertHandler : IAlertNotificationHandler
{
    private readonly ILogger<EmailAlertHandler> _logger;

    public EmailAlertHandler(ILogger<EmailAlertHandler> logger)
    {
        _logger = logger;
    }

    public async Task<bool> SendAsync(string recipient, string message)
    {
        try
        {
            // Placeholder for email service
            _logger.LogInformation("Would send email to {Recipient}: {Message}", recipient, message);
            // In production, integrate with SendGrid, AWS SES, etc.
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send email to {Recipient}", recipient);
            return false;
        }
    }
}

public class SlackAlertHandler : IAlertNotificationHandler
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<SlackAlertHandler> _logger;

    public SlackAlertHandler(HttpClient httpClient, ILogger<SlackAlertHandler> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<bool> SendAsync(string recipient, string message)
    {
        try
        {
            // recipient should be webhook URL
            if (!recipient.StartsWith("https://"))
                return false;

            var payload = new
            {
                text = message,
                attachments = new[]
                {
                    new
                    {
                        color = "warning",
                        title = "Agent Budget Alert",
                        text = message,
                        ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    }
                }
            };

            var response = await _httpClient.PostAsJsonAsync(recipient, payload);
            var success = response.IsSuccessStatusCode;

            if (!success)
                _logger.LogWarning("Slack webhook returned {StatusCode}", response.StatusCode);

            return success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send Slack message");
            return false;
        }
    }
}

public class AlertNotificationService
{
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AuditService _auditService;
    private readonly ILogger<AlertNotificationService> _logger;
    private readonly Dictionary<string, IAlertNotificationHandler> _handlers;

    public AlertNotificationService(
        IDbContextFactory<AppDbContext> dbContextFactory,
        IHttpClientFactory httpClientFactory,
        AuditService auditService,
        ILogger<AlertNotificationService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _httpClientFactory = httpClientFactory;
        _auditService = auditService;
        _logger = logger;

        // Initialize handlers
        _handlers = new()
        {
            ["Email"] = new EmailAlertHandler(logger),
            ["Slack"] = new SlackAlertHandler(httpClientFactory.CreateClient(), logger)
        };
    }

    public async Task<bool> ProcessPendingAlertsAsync()
    {
        using (var db = await _dbContextFactory.CreateDbContextAsync())
        {
            var unnotifiedAlerts = await db.CostAlerts
                .Where(a => !a.IsNotified)
                .Include(a => a.Agent)
                .ToListAsync();

            var processedCount = 0;
            foreach (var alert in unnotifiedAlerts)
            {
                var sent = await SendAlertAsync(db, alert);
                if (sent)
                    processedCount++;
            }

            return processedCount > 0;
        }
    }

    private async Task<bool> SendAlertAsync(AppDbContext db, CostAlert alert)
    {
        var config = await db.AlertNotificationConfigs.FirstOrDefaultAsync(c => c.AgentId == alert.AgentId);
        if (config == null)
            return false; // No notification config

        var rule = await db.AlertEscalationRules
            .FirstOrDefaultAsync(r =>
                r.AgentId == alert.AgentId &&
                r.ThresholdPercent == alert.ThresholdPercent &&
                r.IsActive);

        // Check if notification for this threshold is enabled
        var shouldNotify = alert.ThresholdPercent switch
        {
            50 => config.NotifyAt50Percent,
            75 => config.NotifyAt75Percent,
            90 => config.NotifyAt90Percent,
            100 => config.NotifyAt100Percent,
            _ => false
        };

        if (!shouldNotify)
            return false;

        // Check if duplicate alert was sent recently
        var recentAlert = await db.AlertNotificationHistories
            .Where(h =>
                h.AgentId == alert.AgentId &&
                h.ThresholdPercent == alert.ThresholdPercent &&
                h.SentAt > DateTime.UtcNow.AddMinutes(-config.MinutesBeforeDuplicateAlert))
            .FirstOrDefaultAsync();

        if (recentAlert != null)
            return false; // Already notified recently

        var message = rule?.MessageTemplate ?? BuildDefaultMessage(alert);

        // Send via configured channels
        var notificationSent = false;

        if (!string.IsNullOrEmpty(config.EmailAddress))
        {
            var emailSent = await SendViaChannelAsync(db, alert, config.EmailAddress, "Email", message);
            notificationSent = notificationSent || emailSent;
        }

        if (!string.IsNullOrEmpty(config.SlackWebhookUrl))
        {
            var slackSent = await SendViaChannelAsync(db, alert, config.SlackWebhookUrl, "Slack", message);
            notificationSent = notificationSent || slackSent;
        }

        if (notificationSent)
        {
            alert.IsNotified = true;
            db.CostAlerts.Update(alert);
            await db.SaveChangesAsync();

            await _auditService.LogAsync(
                action: "SendAlert",
                entityType: "CostAlert",
                entityId: alert.Id.ToString(),
                entityName: $"Alert #{alert.Id}",
                afterSnapshot: alert,
                changeDescription: $"{alert.ThresholdPercent}% {alert.Period} budget alert sent"
            );
        }

        return notificationSent;
    }

    private async Task<bool> SendViaChannelAsync(
        AppDbContext db,
        CostAlert alert,
        string recipient,
        string notificationType,
        string message)
    {
        try
        {
            if (!_handlers.TryGetValue(notificationType, out var handler))
            {
                _logger.LogWarning("No handler found for notification type {Type}", notificationType);
                return false;
            }

            var sent = await handler.SendAsync(recipient, message);

            var history = new AlertNotificationHistory
            {
                AgentId = alert.AgentId,
                CostAlertId = alert.Id,
                NotificationType = notificationType,
                Recipient = recipient,
                ThresholdPercent = alert.ThresholdPercent,
                Message = message,
                WasSent = sent,
                SentAt = DateTime.UtcNow
            };

            db.AlertNotificationHistories.Add(history);
            await db.SaveChangesAsync();

            _logger.LogInformation(
                "Alert sent via {Type} for agent {AgentId}: {Result}",
                notificationType, alert.AgentId, sent ? "Success" : "Failed");

            return sent;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending alert via {Type} for agent {AgentId}", notificationType, alert.AgentId);

            var history = new AlertNotificationHistory
            {
                AgentId = alert.AgentId,
                CostAlertId = alert.Id,
                NotificationType = notificationType,
                Recipient = recipient,
                ThresholdPercent = alert.ThresholdPercent,
                Message = message,
                WasSent = false,
                ErrorMessage = ex.Message,
                SentAt = DateTime.UtcNow
            };

            db.AlertNotificationHistories.Add(history);
            await db.SaveChangesAsync();

            return false;
        }
    }

    public async Task<bool> ConfigureNotificationsAsync(
        int agentId,
        string? emailAddress = null,
        string? slackWebhookUrl = null,
        string? slackChannelName = null)
    {
        using (var db = await _dbContextFactory.CreateDbContextAsync())
        {
            var config = await db.AlertNotificationConfigs.FirstOrDefaultAsync(c => c.AgentId == agentId);

            if (config == null)
            {
                config = new AlertNotificationConfig
                {
                    AgentId = agentId,
                    EmailAddress = emailAddress,
                    SlackWebhookUrl = slackWebhookUrl,
                    SlackChannelName = slackChannelName,
                    CreatedAt = DateTime.UtcNow
                };
                db.AlertNotificationConfigs.Add(config);
            }
            else
            {
                if (emailAddress != null) config.EmailAddress = emailAddress;
                if (slackWebhookUrl != null) config.SlackWebhookUrl = slackWebhookUrl;
                if (slackChannelName != null) config.SlackChannelName = slackChannelName;
                config.UpdatedAt = DateTime.UtcNow;
                db.AlertNotificationConfigs.Update(config);
            }

            await db.SaveChangesAsync();
            return true;
        }
    }

    public async Task<bool> AcknowledgeAlertAsync(
        int costAlertId,
        string acknowledgedBy,
        string actionTaken,
        string? notes = null)
    {
        using (var db = await _dbContextFactory.CreateDbContextAsync())
        {
            var alert = await db.CostAlerts.FindAsync(costAlertId);
            if (alert == null)
                return false;

            var acknowledgment = new AlertAcknowledgment
            {
                CostAlertId = costAlertId,
                AcknowledgedBy = acknowledgedBy,
                ActionTaken = actionTaken,
                Notes = notes,
                AcknowledgedAt = DateTime.UtcNow
            };

            db.AlertAcknowledgments.Add(acknowledgment);

            alert.AcknowledgedAt = DateTime.UtcNow;
            alert.AcknowledgedBy = acknowledgedBy;
            db.CostAlerts.Update(alert);

            await db.SaveChangesAsync();

            await _auditService.LogAsync(
                action: "AcknowledgeAlert",
                entityType: "CostAlert",
                entityId: alert.Id.ToString(),
                entityName: $"Alert #{alert.Id}",
                changeDescription: $"Alert acknowledged by {acknowledgedBy}: {actionTaken}"
            );

            return true;
        }
    }

    private static string BuildDefaultMessage(CostAlert alert)
    {
        var percent = alert.ThresholdPercent;
        var severity = percent switch
        {
            50 => "⚠️ Warning",
            75 => "⚠️ Caution",
            90 => "🚨 Critical",
            100 => "❌ Exceeded",
            _ => "⚠️ Alert"
        };

        return $@"{severity}
Budget Alert for Agent #{alert.Id}

Period: {alert.Period}
Threshold: {alert.ThresholdPercent}%

Current Spending: ${alert.CurrentSpentUSD:F2}
Budget Limit: ${alert.LimitUSD:F2}
Usage: {(int)((alert.CurrentSpentUSD / alert.LimitUSD) * 100)}%

Message: {alert.Message}

Time: {alert.CreatedAt:yyyy-MM-dd HH:mm:ss} UTC";
    }
}
