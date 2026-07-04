# Input Validation Guide

This document describes the input validation strategy for Xpedeon Agent Mission Control, including request validation, constraints, and error handling.

## Overview

- **Strategy**: Layered validation (middleware + service-level)
- **Framework**: Custom validation service (FluentValidation pattern)
- **Request Limits**: 10 MB total, 5 MB JSON payload, 32 max JSON depth
- **Coverage**: All POST, PUT, PATCH requests validated
- **Errors**: Consistent 400 status codes with detailed messages

## Architecture

```
HTTP Request
    ↓
ValidationMiddleware (size check, JSON format)
    ↓
RequestDTO deserialization
    ↓
ValidationService (business logic rules)
    ↓
Service Layer (execution)
```

## Request Limits

| Limit | Value | Applies To |
|-------|-------|-----------|
| Max Request Size | 10 MB | All requests |
| Max JSON Payload | 5 MB | JSON bodies |
| Max JSON Depth | 32 levels | Nested objects |
| Max Field Size | 50,000 chars | Task input, prompts |

## Validators

### CreateAgentValidator

```csharp
var result = validationService.ValidateCreateAgent(request);
if (!result.IsValid)
    return BadRequest(new { errors = result.Errors });
```

**Rules:**
- Name: Required, max 255 chars
- Description: Optional, max 2,000 chars
- SystemPrompt: Optional, max 10,000 chars
- MaxSpawns: 0-100
- MaxSpawnDepth: 0-10
- SpawnStrategy: RoundRobin | Sequential | Random
- AggregationStrategy: CollectAll | FirstWins | Voting | LLMSynthesize

**Examples:**

✅ Valid:
```json
{
  "name": "Customer Service Agent",
  "description": "Handles customer inquiries",
  "systemPrompt": "You are a helpful customer service agent...",
  "maxSpawns": 5,
  "maxSpawnDepth": 2,
  "spawnStrategy": "Sequential"
}
```

❌ Invalid:
```json
{
  "name": "",  // Name required
  "maxSpawns": 101,  // Max 100
  "maxSpawnDepth": 15  // Max 10
}
```

### CreateTaskValidator

```csharp
var result = validationService.ValidateCreateTask(request);
```

**Rules:**
- AgentId: Required, > 0
- Input: Required, max 50,000 chars
- Priority: Low | Normal | High | Urgent
- RequiresApproval: Optional boolean

**Examples:**

✅ Valid:
```json
{
  "agentId": 1,
  "input": "Please analyze this customer feedback...",
  "priority": "High",
  "requiresApproval": true
}
```

❌ Invalid:
```json
{
  "agentId": 0,  // Must be > 0
  "input": "",  // Input required
  "priority": "Critical"  // Invalid priority
}
```

### CreateLLMProviderValidator

```csharp
var result = validationService.ValidateCreateLLMProvider(request);
```

**Rules:**
- Name: Required, max 255 chars
- ProviderType: Claude | OpenAI | Ollama | Custom
- ModelName: Required, max 255 chars
- ApiKey: Required, 10-500 chars
- Endpoint: Optional, must be valid HTTPS URL
- MaxTokens: 1-100,000
- Temperature: 0.0-2.0
- CostPer1kInputTokens: Non-negative decimal
- CostPer1kOutputTokens: Non-negative decimal

**Examples:**

✅ Valid:
```json
{
  "name": "Claude 3 Sonnet",
  "providerType": "Claude",
  "modelName": "claude-3-sonnet-20240229",
  "apiKey": "sk-ant-v3-...",
  "maxTokens": 4096,
  "temperature": 0.7,
  "costPer1kInputTokens": 0.003,
  "costPer1kOutputTokens": 0.015
}
```

❌ Invalid:
```json
{
  "name": "",  // Name required
  "providerType": "GPT",  // Invalid type
  "apiKey": "sk",  // Min 10 chars
  "temperature": 2.5  // Max 2.0
}
```

### CreateSkillValidator

```csharp
var result = validationService.ValidateCreateSkill(request);
```

**Rules:**
- Name: Required, max 255 chars
- PromptSnippet: Required, max 5,000 chars
- MCPToolAllowList: Optional, must be valid JSON array
- Category: Optional, max 255 chars

### CreateWorkflowValidator

```csharp
var result = validationService.ValidateCreateWorkflow(request);
```

**Rules:**
- Name: Required, max 255 chars
- DefinitionJson: Required, must be valid JSON

### CreateCapabilityValidator

```csharp
var result = validationService.ValidateCreateCapability(request);
```

**Rules:**
- Name: Required, max 255 chars
- CapabilityType: Required
- PayloadJson: Required, must be valid JSON

## ValidationMiddleware

Automatic validation for all POST/PUT/PATCH requests:

```csharp
// In Program.cs
app.UseValidationMiddleware();
```

### Enforced Rules

1. **Request Size** (413 Payload Too Large)
   ```
   Request > 10 MB → 413 error
   ```

2. **JSON Validity** (400 Bad Request)
   ```
   Malformed JSON → 400 error with details
   ```

3. **Depth Limit** (400 Bad Request)
   ```
   JSON depth > 32 → 400 error
   ```

### Error Responses

**Payload Too Large:**
```json
{
  "error": "PayloadTooLarge",
  "message": "Request size exceeds maximum of 10 MB",
  "maxSize": 10485760
}
```

**Invalid JSON:**
```json
{
  "error": "InvalidJson",
  "message": "Request body contains invalid JSON",
  "details": "Invalid character in JSON at position 123"
}
```

**Validation Failed:**
```json
{
  "error": "ValidationError",
  "message": [
    "Agent name is required",
    "MaxSpawns must be between 0 and 100"
  ]
}
```

## Using Validation in Services

### Example: Create Agent with Validation

```csharp
[HttpPost("api/agents")]
public async Task<IActionResult> CreateAgent(CreateAgentRequest request)
{
    // Service-level validation
    var validation = _validationService.ValidateCreateAgent(request);
    if (!validation.IsValid)
    {
        return BadRequest(new
        {
            error = "ValidationError",
            details = validation.Errors
        });
    }

    // Create agent
    var agent = new Agent
    {
        Name = request.Name,
        Description = request.Description,
        SystemPrompt = request.SystemPrompt,
        // ... other fields
    };

    await _agentService.CreateAsync(agent);
    return CreatedAtAction(nameof(GetAgent), new { id = agent.Id }, agent);
}
```

### Example: Create Task with Validation

```csharp
[HttpPost("api/tasks")]
public async Task<IActionResult> CreateTask(CreateTaskRequest request)
{
    var validation = _validationService.ValidateCreateTask(request);
    if (!validation.IsValid)
        return BadRequest(validation.Errors);

    var task = new AgentTask
    {
        AgentId = request.AgentId,
        Input = request.Input,
        Priority = request.Priority,
        // ...
    };

    await _taskService.CreateAndRunAsync(task);
    return CreatedAtAction(nameof(GetTask), new { id = task.Id }, task);
}
```

## Security Considerations

### Injection Prevention
- ✅ JSON depth limits prevent DOS attacks
- ✅ Size limits prevent memory exhaustion
- ✅ Field length limits prevent buffer overflows
- ✅ URL validation prevents injection into endpoints
- ✅ Strategy validation prevents command injection

### What This Protects Against
- JSON bomb attacks (depth limit = 32)
- DoS via massive payloads (size limits)
- Buffer overflows (field length limits)
- NoSQL injection (JSON type checking)
- SSRF attacks (URL validation for endpoints)

### What This Doesn't Protect Against
- SQL injection (use parameterized queries in EF Core)
- Authentication bypass (handled by auth middleware)
- Authorization issues (use policy-based auth)
- XSS (handled by Blazor escaping)

## Monitoring & Alerting

### Log Rejected Requests

```csharp
// Automatically logged when validation fails
logger.LogWarning("Request rejected: payload too large ({Size} bytes)", size);
logger.LogWarning("JSON validation failed: {Message}", ex.Message);
```

### Metrics to Track
- Total requests validated
- Rejected request count (by reason)
- Average request size
- P95/P99 request size
- Validation error frequency

### Alerts
- ⚠️ Sustained high rejection rate (>5% of requests)
- ⚠️ Spike in oversized requests
- ⚠️ Repeated invalid JSON patterns (possible attack)

## Testing

### Unit Tests

```csharp
[Fact]
public void ValidateCreateAgent_InvalidName_ReturnsFalse()
{
    var request = new CreateAgentRequest
    {
        Name = "", // Invalid
    };

    var result = _validationService.ValidateCreateAgent(request);

    Assert.False(result.IsValid);
    Assert.Contains("name is required", result.Errors[0]);
}

[Fact]
public void ValidateCreateTask_ExceedsMaxLength_ReturnsFalse()
{
    var request = new CreateTaskRequest
    {
        AgentId = 1,
        Input = new string('x', 50001) // Exceeds max
    };

    var result = _validationService.ValidateCreateTask(request);

    Assert.False(result.IsValid);
}
```

### Integration Tests

```csharp
[Fact]
public async Task ValidateMiddleware_OversizedPayload_Returns413()
{
    var largePayload = new string('x', 11 * 1024 * 1024); // 11 MB
    var content = new StringContent(largePayload);

    var response = await _client.PostAsync("/api/agents", content);

    Assert.Equal(HttpStatusCode.PayloadTooLarge, response.StatusCode);
}

[Fact]
public async Task ValidateMiddleware_InvalidJson_Returns400()
{
    var content = new StringContent("{invalid json}");
    content.Headers.ContentType = new("application/json");

    var response = await _client.PostAsync("/api/agents", content);

    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
}
```

## References

- PRODUCTION_ROADMAP.md - Phase 1.4 validation requirements
- OWASP Input Validation Cheat Sheet
- ASP.NET Core Security Best Practices
