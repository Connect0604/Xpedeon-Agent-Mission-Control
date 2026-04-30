using XpedeonAgentMissionControl.Services;

namespace XpedeonAgentMissionControl.Middleware;

public class RateLimitingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<RateLimitingMiddleware> _logger;

    public RateLimitingMiddleware(RequestDelegate next, ILogger<RateLimitingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, RateLimitingService rateLimitingService)
    {
        // Skip rate limiting for non-API requests and health checks
        var path = context.Request.Path.Value ?? "";
        if (IsExemptPath(path))
        {
            await _next(context);
            return;
        }

        // Extract agent ID from request (from header, query, or route)
        if (!TryExtractAgentId(context, out var agentId))
        {
            await _next(context);
            return;
        }

        // Check rate limit
        var checkResult = await rateLimitingService.CheckRateLimitAsync(agentId);

        if (!checkResult.IsAllowed)
        {
            if (checkResult.ShouldQueue)
            {
                _logger.LogWarning(
                    "Task queued due to rate limit ({Reason}) for agent {AgentId}",
                    checkResult.Reason, agentId);

                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = "Rate limit exceeded",
                    reason = checkResult.Reason,
                    message = "Your task has been queued and will execute when limits allow",
                    retryAfter = 60
                });
                return;
            }

            if (checkResult.IsDegraded)
            {
                _logger.LogWarning(
                    "Request degraded due to rate limit ({Reason}) for agent {AgentId}",
                    checkResult.Reason, agentId);

                // Add header indicating degraded mode
                context.Response.Headers.Add("X-Rate-Limit-Degraded", "true");
            }
        }

        await _next(context);
    }

    private static bool IsExemptPath(string path)
    {
        var exemptPaths = new[]
        {
            "/health",
            "/health/detailed",
            "/.well-known",
            "/swagger",
            "/metrics",
            "/api/auth",
            "/hubs/agent"
        };

        return exemptPaths.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryExtractAgentId(HttpContext context, out int agentId)
    {
        agentId = 0;

        // Try header first
        if (context.Request.Headers.TryGetValue("X-Agent-Id", out var headerValue))
        {
            if (int.TryParse(headerValue.ToString(), out var id))
            {
                agentId = id;
                return true;
            }
        }

        // Try query parameter
        if (context.Request.Query.TryGetValue("agentId", out var queryValue))
        {
            if (int.TryParse(queryValue.ToString(), out var id))
            {
                agentId = id;
                return true;
            }
        }

        // Try route
        if (context.GetRouteData()?.Values.TryGetValue("agentId", out var routeValue) == true)
        {
            if (int.TryParse(routeValue?.ToString(), out var id))
            {
                agentId = id;
                return true;
            }
        }

        return false;
    }
}

public static class RateLimitingMiddlewareExtensions
{
    public static IApplicationBuilder UseRateLimitingMiddleware(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<RateLimitingMiddleware>();
    }
}
