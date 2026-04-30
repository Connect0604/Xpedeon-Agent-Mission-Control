using System.Text;
using System.Text.Json;
using Microsoft.IO;
using XpedeonAgentMissionControl.Services;

namespace XpedeonAgentMissionControl.Middleware;

/// <summary>
/// Middleware for validating incoming HTTP requests
/// - Enforces request size limits
/// - Validates JSON payloads
/// - Logs validation errors
/// </summary>
public class ValidationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ValidationMiddleware> _logger;
    private readonly RecyclableMemoryStreamManager _memoryStreamManager;

    // Configuration
    private const int MaxRequestSizeBytes = 10 * 1024 * 1024; // 10 MB
    private const int MaxPayloadSizeBytes = 5 * 1024 * 1024;  // 5 MB
    private const int MaxJsonDepth = 32;

    public ValidationMiddleware(RequestDelegate next, ILogger<ValidationMiddleware> logger)
    {
        _next = next;
        _logger = logger;
        _memoryStreamManager = new RecyclableMemoryStreamManager();
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Only validate POST, PUT, PATCH requests with bodies
        if (!ShouldValidate(context.Request))
        {
            await _next(context);
            return;
        }

        try
        {
            // Check request size
            if (context.Request.ContentLength > MaxRequestSizeBytes)
            {
                context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                context.Response.ContentType = "application/json";

                var errorResponse = new
                {
                    error = "PayloadTooLarge",
                    message = $"Request size exceeds maximum of {MaxRequestSizeBytes / 1024 / 1024} MB",
                    maxSize = MaxRequestSizeBytes
                };

                await context.Response.WriteAsJsonAsync(errorResponse);
                _logger.LogWarning("Request rejected: payload too large ({Size} bytes)", context.Request.ContentLength);
                return;
            }

            // Validate JSON payload
            if (!await ValidateJsonPayloadAsync(context.Request))
            {
                return; // Response already written by ValidateJsonPayloadAsync
            }

            // Proceed to next middleware
            await _next(context);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Validation middleware error");

            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                context.Response.ContentType = "application/json";

                var errorResponse = new
                {
                    error = "ValidationError",
                    message = "Request validation failed"
                };

                await context.Response.WriteAsJsonAsync(errorResponse);
            }
        }
    }

    private bool ShouldValidate(HttpRequest request)
    {
        // Only validate requests with bodies
        if (request.ContentLength == null || request.ContentLength == 0)
            return false;

        // Only validate POST, PUT, PATCH
        if (!request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
            !request.Method.Equals("PUT", StringComparison.OrdinalIgnoreCase) &&
            !request.Method.Equals("PATCH", StringComparison.OrdinalIgnoreCase))
            return false;

        // Only validate JSON content
        return request.ContentType?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true;
    }

    private async Task<bool> ValidateJsonPayloadAsync(HttpRequest request)
    {
        try
        {
            // Read body into memory stream (without consuming the original stream)
            using (var memoryStream = _memoryStreamManager.GetStream())
            {
                await request.Body.CopyToAsync(memoryStream);
                memoryStream.Seek(0, SeekOrigin.Begin);

                // Check payload size
                if (memoryStream.Length > MaxPayloadSizeBytes)
                {
                    request.HttpContext.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                    request.HttpContext.Response.ContentType = "application/json";

                    var errorResponse = new
                    {
                        error = "PayloadTooLarge",
                        message = $"JSON payload exceeds maximum of {MaxPayloadSizeBytes / 1024 / 1024} MB",
                        maxSize = MaxPayloadSizeBytes,
                        actual = memoryStream.Length
                    };

                    await request.HttpContext.Response.WriteAsJsonAsync(errorResponse);
                    _logger.LogWarning("JSON payload rejected: too large ({Size} bytes)", memoryStream.Length);
                    return false;
                }

                // Try to parse JSON
                try
                {
                    var options = new JsonSerializerOptions { MaxDepth = MaxJsonDepth };
                    memoryStream.Seek(0, SeekOrigin.Begin);
                    using (var reader = new StreamReader(memoryStream))
                    {
                        var json = await reader.ReadToEndAsync();
                        JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = MaxJsonDepth });
                    }
                }
                catch (JsonException ex)
                {
                    request.HttpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
                    request.HttpContext.Response.ContentType = "application/json";

                    var errorResponse = new
                    {
                        error = "InvalidJson",
                        message = "Request body contains invalid JSON",
                        details = ex.Message
                    };

                    await request.HttpContext.Response.WriteAsJsonAsync(errorResponse);
                    _logger.LogWarning("JSON validation failed: {Message}", ex.Message);
                    return false;
                }

                // Reset stream for next middleware
                memoryStream.Seek(0, SeekOrigin.Begin);
                request.Body = memoryStream;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "JSON payload validation error");

            request.HttpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            request.HttpContext.Response.ContentType = "application/json";

            var errorResponse = new
            {
                error = "ValidationError",
                message = "Failed to validate request body"
            };

            await request.HttpContext.Response.WriteAsJsonAsync(errorResponse);
            return false;
        }
    }
}

/// <summary>
/// Extension method to register validation middleware
/// </summary>
public static class ValidationMiddlewareExtensions
{
    public static IApplicationBuilder UseValidationMiddleware(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<ValidationMiddleware>();
    }
}
