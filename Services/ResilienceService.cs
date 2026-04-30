using Microsoft.EntityFrameworkCore;
using XpedeonAgentMissionControl.Data;
using XpedeonAgentMissionControl.Models;

namespace XpedeonAgentMissionControl.Services;

public class ResilienceService
{
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly AuditService _auditService;
    private readonly ILogger<ResilienceService> _logger;

    public ResilienceService(
        IDbContextFactory<AppDbContext> dbContextFactory,
        AuditService auditService,
        ILogger<ResilienceService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _auditService = auditService;
        _logger = logger;
    }

    public async Task<T> ExecuteWithResilienceAsync<T>(
        int agentId,
        int taskId,
        int providerId,
        Func<Task<T>> action,
        CancellationToken cancellationToken = default)
    {
        var retryPolicy = await GetRetryPolicyAsync(providerId);
        var attemptNumber = await GetNextAttemptNumberAsync(taskId);
        var attempt = await StartAttemptAsync(agentId, taskId, attemptNumber);

        var startTime = DateTime.UtcNow;

        try
        {
            // Check circuit breaker first
            var cbState = await GetCircuitBreakerStateAsync(providerId);
            if (cbState?.IsOpen == true)
            {
                await FailAttemptAsync(attempt, "CircuitBreakerOpen",
                    $"Circuit breaker is open for provider {providerId}. Next reset: {cbState.NextResetAt}");

                throw new CircuitBreakerOpenException(
                    $"Provider {providerId} circuit breaker is open until {cbState.NextResetAt}",
                    cbState.NextResetAt);
            }

            T result = default!;
            Exception? lastException = null;

            // Execute with retry loop
            for (var attempt_n = 1; attempt_n <= (retryPolicy?.MaxAttempts ?? 1) + 1; attempt_n++)
            {
                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    cts.CancelAfter(TimeSpan.FromSeconds(retryPolicy?.TimeoutSeconds ?? 120));

                    result = await action();

                    // Success: update circuit breaker
                    await RecordCircuitBreakerSuccessAsync(providerId);
                    await CompleteAttemptAsync(attempt, "Success",
                        (long)(DateTime.UtcNow - startTime).TotalMilliseconds);

                    return result;
                }
                catch (Exception ex) when (IsRetryable(ex, retryPolicy) && attempt_n <= retryPolicy?.MaxAttempts)
                {
                    lastException = ex;

                    var delay = CalculateBackoff(retryPolicy, attempt_n);

                    _logger.LogWarning(
                        "Task {TaskId} attempt {Attempt} failed: {Error}. Retrying in {Delay}ms...",
                        taskId, attempt_n, ex.Message, delay);

                    await Task.Delay(delay, cancellationToken);
                }
            }

            // All attempts exhausted
            await RecordCircuitBreakerFailureAsync(providerId);
            await FailAttemptAsync(attempt, "Failed", lastException?.Message ?? "Unknown error",
                (long)(DateTime.UtcNow - startTime).TotalMilliseconds);

            throw lastException ?? new InvalidOperationException("All retry attempts exhausted");
        }
        catch (Exception ex) when (ex is not CircuitBreakerOpenException)
        {
            await RecordCircuitBreakerFailureAsync(providerId);
            await FailAttemptAsync(attempt, "Failed", ex.Message,
                (long)(DateTime.UtcNow - startTime).TotalMilliseconds);
            throw;
        }
    }

    public async Task SaveCheckpointAsync(
        int taskId,
        string label,
        string? stateJson = null,
        int iteration = 0,
        string? partialOutput = null,
        int tokensUsed = 0,
        decimal costUSD = 0)
    {
        using var db = await _dbContextFactory.CreateDbContextAsync();

        var checkpoint = new TaskCheckpoint
        {
            TaskId = taskId,
            Label = label,
            StateJson = stateJson,
            Iteration = iteration,
            PartialOutput = partialOutput,
            TokensUsed = tokensUsed,
            CostUSD = costUSD,
            IsRecoverable = true,
            SavedAt = DateTime.UtcNow
        };

        db.TaskCheckpoints.Add(checkpoint);
        await db.SaveChangesAsync();

        _logger.LogDebug("Checkpoint saved for task {TaskId}: {Label}", taskId, label);
    }

    public async Task<TaskCheckpoint?> GetLatestCheckpointAsync(int taskId)
    {
        using var db = await _dbContextFactory.CreateDbContextAsync();
        return await db.TaskCheckpoints
            .Where(c => c.TaskId == taskId && c.IsRecoverable)
            .OrderByDescending(c => c.SavedAt)
            .FirstOrDefaultAsync();
    }

    public async Task<bool> CanResumeFromCheckpointAsync(int taskId)
    {
        var checkpoint = await GetLatestCheckpointAsync(taskId);
        return checkpoint != null;
    }

    public async Task<ProviderResilienceStatus?> GetProviderStatusAsync(int providerId)
    {
        using var db = await _dbContextFactory.CreateDbContextAsync();

        var provider = await db.LLMProviders.FindAsync(providerId);
        if (provider == null) return null;

        var cbState = await db.CircuitBreakerStates.FirstOrDefaultAsync(c => c.ProviderId == providerId);
        var now = DateTime.UtcNow;
        var yesterday = now.AddDays(-1);

        var recentAttempts = await db.TaskExecutionAttempts
            .Where(a => a.Task!.Agent!.LLMProviderId == providerId && a.StartedAt >= yesterday)
            .ToListAsync();

        var successCount = recentAttempts.Count(a => a.Outcome == "Success");
        var failureCount = recentAttempts.Count(a => a.Outcome == "Failed");
        var totalCount = successCount + failureCount;

        return new ProviderResilienceStatus
        {
            ProviderId = providerId,
            ProviderName = provider.Name,
            CircuitState = cbState?.State ?? "Closed",
            ConsecutiveFailures = cbState?.ConsecutiveFailures ?? 0,
            SuccessLast24h = successCount,
            FailuresLast24h = failureCount,
            SuccessRateLast24h = totalCount > 0 ? (double)successCount / totalCount : 1.0,
            CircuitOpenedAt = cbState?.LastOpenedAt,
            CircuitResetAt = cbState?.NextResetAt,
            AsOfDate = now
        };
    }

    public async Task InitializeRetryPolicyAsync(int providerId, int maxAttempts = 3, int baseDelayMs = 1000)
    {
        using var db = await _dbContextFactory.CreateDbContextAsync();

        var existing = await db.RetryPolicies.FirstOrDefaultAsync(p => p.ProviderId == providerId);
        if (existing != null) return;

        var policy = new RetryPolicy
        {
            ProviderId = providerId,
            MaxAttempts = maxAttempts,
            BaseDelayMs = baseDelayMs,
            MaxDelayMs = 30000,
            BackoffMultiplier = 2.0,
            JitterFactor = 0.1,
            RetryOnStatusCodes = "429,500,502,503,504",
            RetryOnTimeout = true,
            TimeoutSeconds = 120,
            CreatedAt = DateTime.UtcNow
        };

        db.RetryPolicies.Add(policy);
        await db.SaveChangesAsync();
    }

    public async Task ResetCircuitBreakerAsync(int providerId)
    {
        using var db = await _dbContextFactory.CreateDbContextAsync();

        var cbState = await db.CircuitBreakerStates.FirstOrDefaultAsync(c => c.ProviderId == providerId);
        if (cbState == null) return;

        cbState.State = "Closed";
        cbState.ConsecutiveFailures = 0;
        cbState.NextResetAt = null;
        cbState.LastTransitionAt = DateTime.UtcNow;

        db.CircuitBreakerStates.Update(cbState);
        await db.SaveChangesAsync();

        _logger.LogInformation("Circuit breaker manually reset for provider {ProviderId}", providerId);

        await _auditService.LogAsync(
            action: "ResetCircuitBreaker",
            entityType: "CircuitBreakerState",
            entityId: cbState.Id.ToString(),
            changeDescription: $"Circuit breaker manually reset for provider {providerId}"
        );

    }

    private async Task<RetryPolicy?> GetRetryPolicyAsync(int providerId)
    {
        using var db = await _dbContextFactory.CreateDbContextAsync();
        return await db.RetryPolicies.FirstOrDefaultAsync(p => p.ProviderId == providerId);
    }

    private async Task<CircuitBreakerState?> GetCircuitBreakerStateAsync(int providerId)
    {
        using var db = await _dbContextFactory.CreateDbContextAsync();
        var state = await db.CircuitBreakerStates.FirstOrDefaultAsync(c => c.ProviderId == providerId);

        if (state?.State == "Open" && state.NextResetAt <= DateTime.UtcNow)
        {
            // Auto-transition to HalfOpen
            state.State = "HalfOpen";
            state.LastTransitionAt = DateTime.UtcNow;
            db.CircuitBreakerStates.Update(state);
            await db.SaveChangesAsync();

            _logger.LogInformation(
                "Circuit breaker for provider {ProviderId} transitioned to HalfOpen",
                providerId);
        }

        return state;
    }

    private async Task RecordCircuitBreakerSuccessAsync(int providerId)
    {
        using var db = await _dbContextFactory.CreateDbContextAsync();
        var state = await db.CircuitBreakerStates.FirstOrDefaultAsync(c => c.ProviderId == providerId);

        if (state == null)
        {
            db.CircuitBreakerStates.Add(new CircuitBreakerState
            {
                ProviderId = providerId,
                State = "Closed",
                SuccessCount = 1
            });
        }
        else
        {
            state.SuccessCount++;
            state.ConsecutiveFailures = 0;

            if (state.State == "HalfOpen")
            {
                // Check if enough successes to close
                if (state.SuccessCount >= state.HalfOpenSuccessThreshold)
                {
                    state.State = "Closed";
                    state.LastTransitionAt = DateTime.UtcNow;
                    state.NextResetAt = null;
                    _logger.LogInformation("Circuit breaker closed for provider {ProviderId}", providerId);
                }
            }

            db.CircuitBreakerStates.Update(state);
        }

        await db.SaveChangesAsync();
    }

    private async Task RecordCircuitBreakerFailureAsync(int providerId)
    {
        using var db = await _dbContextFactory.CreateDbContextAsync();
        var state = await db.CircuitBreakerStates.FirstOrDefaultAsync(c => c.ProviderId == providerId);

        if (state == null)
        {
            state = new CircuitBreakerState
            {
                ProviderId = providerId,
                State = "Closed",
                ConsecutiveFailures = 1,
                FailureCount = 1
            };
            db.CircuitBreakerStates.Add(state);
        }
        else
        {
            state.FailureCount++;
            state.ConsecutiveFailures++;

            if (state.ConsecutiveFailures >= state.FailureThreshold && state.State != "Open")
            {
                state.State = "Open";
                state.LastOpenedAt = DateTime.UtcNow;
                state.NextResetAt = DateTime.UtcNow.AddSeconds(state.OpenDurationSeconds);
                state.LastTransitionAt = DateTime.UtcNow;

                _logger.LogWarning(
                    "Circuit breaker opened for provider {ProviderId} after {Failures} failures. Reset at {ResetAt}",
                    providerId, state.ConsecutiveFailures, state.NextResetAt);

                await _auditService.LogAsync(
                    action: "CircuitBreakerOpened",
                    entityType: "CircuitBreakerState",
                    entityId: state.Id.ToString(),
                    changeDescription: $"Circuit breaker opened for provider {providerId} after {state.ConsecutiveFailures} consecutive failures"
                );
            }

            db.CircuitBreakerStates.Update(state);
        }

        await db.SaveChangesAsync();
    }

    private async Task<int> GetNextAttemptNumberAsync(int taskId)
    {
        using var db = await _dbContextFactory.CreateDbContextAsync();
        var maxAttempt = await db.TaskExecutionAttempts
            .Where(a => a.TaskId == taskId)
            .MaxAsync(a => (int?)a.AttemptNumber) ?? 0;
        return maxAttempt + 1;
    }

    private async Task<TaskExecutionAttempt> StartAttemptAsync(int agentId, int taskId, int attemptNumber)
    {
        using var db = await _dbContextFactory.CreateDbContextAsync();
        var attempt = new TaskExecutionAttempt
        {
            AgentId = agentId,
            TaskId = taskId,
            AttemptNumber = attemptNumber,
            StartedAt = DateTime.UtcNow,
            Outcome = "InProgress"
        };
        db.TaskExecutionAttempts.Add(attempt);
        await db.SaveChangesAsync();
        return attempt;
    }

    private async Task CompleteAttemptAsync(TaskExecutionAttempt attempt, string outcome, long durationMs)
    {
        using var db = await _dbContextFactory.CreateDbContextAsync();
        var tracked = await db.TaskExecutionAttempts.FindAsync(attempt.Id);
        if (tracked == null) return;

        tracked.Outcome = outcome;
        tracked.CompletedAt = DateTime.UtcNow;
        tracked.DurationMs = durationMs;
        db.TaskExecutionAttempts.Update(tracked);
        await db.SaveChangesAsync();
    }

    private async Task FailAttemptAsync(
        TaskExecutionAttempt attempt,
        string outcome,
        string? error,
        long durationMs = 0)
    {
        using var db = await _dbContextFactory.CreateDbContextAsync();
        var tracked = await db.TaskExecutionAttempts.FindAsync(attempt.Id);
        if (tracked == null) return;

        tracked.Outcome = outcome;
        tracked.CompletedAt = DateTime.UtcNow;
        tracked.ErrorMessage = error;
        tracked.DurationMs = durationMs;
        db.TaskExecutionAttempts.Update(tracked);
        await db.SaveChangesAsync();
    }

    private static bool IsRetryable(Exception ex, RetryPolicy? policy)
    {
        if (policy == null) return false;

        // Always retry timeouts if configured
        if (policy.RetryOnTimeout && ex is TaskCanceledException or TimeoutException)
            return true;

        // Check for HTTP-based exceptions with retryable status codes
        if (ex is HttpRequestException httpEx)
        {
            if (httpEx.Message.Contains("429") || httpEx.Message.Contains("503") || httpEx.Message.Contains("502"))
                return true;
        }

        return false;
    }

    private static int CalculateBackoff(RetryPolicy? policy, int attemptNumber)
    {
        if (policy == null) return 1000;

        // Exponential backoff: base * multiplier^(attempt-1)
        var delay = (int)(policy.BaseDelayMs * Math.Pow(policy.BackoffMultiplier, attemptNumber - 1));

        // Add jitter to prevent thundering herd
        var jitter = (int)(delay * policy.JitterFactor * (Random.Shared.NextDouble() * 2 - 1));
        delay += jitter;

        // Cap at maximum
        return Math.Min(delay, policy.MaxDelayMs);
    }

}

public class CircuitBreakerOpenException : Exception
{
    public DateTime? ResetAt { get; }

    public CircuitBreakerOpenException(string message, DateTime? resetAt = null)
        : base(message)
    {
        ResetAt = resetAt;
    }
}
