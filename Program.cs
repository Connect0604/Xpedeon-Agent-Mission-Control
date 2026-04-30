using Microsoft.EntityFrameworkCore;
using XpedeonAgentMissionControl.Configuration;
using XpedeonAgentMissionControl.Data;
using XpedeonAgentMissionControl.Hubs;
using XpedeonAgentMissionControl.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("database.config.json", optional: false, reloadOnChange: true);

var databaseConfig = builder.Configuration
    .Get<DatabaseConfig>() ?? new DatabaseConfig();
builder.Services.AddSingleton(databaseConfig);
var hermesOpenClawConfig = builder.Configuration
    .GetSection("HermesOpenClaw")
    .Get<HermesOpenClawConfig>() ?? new HermesOpenClawConfig();
builder.Services.AddSingleton(hermesOpenClawConfig);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// SignalR
builder.Services.AddSignalR();

builder.Services.AddDbContextFactory<AppDbContext>(options =>
{
    var provider = (databaseConfig.Provider ?? "SQLite").Trim();
    if (provider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase))
    {
        options.UseSqlServer(
            BuildSqlServerConnectionString(databaseConfig),
            sql => sql.EnableRetryOnFailure(
                maxRetryCount: 5,
                maxRetryDelay: TimeSpan.FromSeconds(10),
                errorNumbersToAdd: null));
    }
    else
    {
        var sqlitePath = databaseConfig.FilePath;
        if (string.IsNullOrWhiteSpace(sqlitePath))
            sqlitePath = "xpedeon.db";

        if (!Path.IsPathRooted(sqlitePath))
            sqlitePath = Path.Combine(builder.Environment.ContentRootPath, sqlitePath);

        options.UseSqlite($"Data Source={sqlitePath}");
    }
});

// Core services
builder.Services.AddScoped<AgentService>();
builder.Services.AddScoped<TaskService>();
builder.Services.AddScoped<LLMProviderService>();
builder.Services.AddScoped<TemplateService>();
builder.Services.AddScoped<ApiKeyService>();
builder.Services.AddScoped<SkillService>();
builder.Services.AddScoped<EvaluationService>();
builder.Services.AddScoped<SwarmService>();
builder.Services.AddScoped<WorkflowService>();
builder.Services.AddScoped<SelfLearningService>();
builder.Services.AddScoped<ScheduleService>();
builder.Services.AddScoped<DynamicSpawnService>();
builder.Services.AddScoped<MCPService>();
builder.Services.AddScoped<IMcpConnectionProbe>(sp => sp.GetRequiredService<MCPService>());
builder.Services.AddScoped<ExternalSkillPackageService>();
builder.Services.AddScoped<HermesOpenClawExecutionService>();
builder.Services.AddHostedService<AgentSchedulerService>();
builder.Services.AddHostedService<HermesOpenClawSyncService>();
builder.Services.AddScoped<MemoryService>();
builder.Services.AddScoped<LLMExecutionService>();
builder.Services.AddScoped<LocalAutomationValidator>();
builder.Services.AddScoped<LocalAutomationExecutor>();
builder.Services.AddScoped<LocalAutomationResponseFormatter>();
builder.Services.AddScoped<LocalCapabilityService>();
builder.Services.AddScoped<PendingLocalCapabilityDraftService>();
builder.Services.AddScoped<LocalCapabilityMatcher>();
builder.Services.AddScoped<LocalCapabilityExecutor>();
builder.Services.AddSingleton(_ => new LocalCapabilityScriptStore(builder.Environment.ContentRootPath));
builder.Services.AddScoped<LocalAutomationOrchestrator>();
builder.Services.AddScoped<LogService>();
builder.Services.AddScoped<TaskExecutionEventService>();
builder.Services.AddSingleton<RealtimeService>();
builder.Services.AddSingleton<MockDataService>(); // kept for sim feed

builder.Services.AddHttpClient();

var app = builder.Build();

// Apply EF Core migrations on startup
try
{
    using (var scope = app.Services.CreateScope())
    {
        var dbContextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        using (var db = await dbContextFactory.CreateDbContextAsync())
        {
            // Apply all pending migrations
            await db.Database.MigrateAsync();
        }
    }
}
catch (Exception ex)
{
    var logger = app.Services.GetRequiredService<ILogger<Program>>();
    logger.LogError(ex, "Failed to apply database migrations on startup. The application will attempt to continue, but database access may fail.");
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<XpedeonAgentMissionControl.Components.App>()
    .AddInteractiveServerRenderMode();

// SignalR hub endpoint
app.MapHub<AgentHub>("/hubs/agent");

app.Run();

static string BuildSqlServerConnectionString(DatabaseConfig config)
{
    if (string.IsNullOrWhiteSpace(config.Server))
        throw new InvalidOperationException("Database server is required for SqlServer provider.");

    if (string.IsNullOrWhiteSpace(config.Database))
        throw new InvalidOperationException("Database name is required for SqlServer provider.");

    var parts = new List<string>
    {
        $"Server={config.Server}",
        $"Database={config.Database}",
        $"TrustServerCertificate={(config.TrustServerCertificate ? "True" : "False")}",
        "Connect Timeout=30"
    };

    if (config.IntegratedSecurity)
    {
        parts.Add("Integrated Security=True");
    }
    else
    {
        parts.Add($"User Id={config.UserId}");
        parts.Add($"Password={config.Password}");
    }

    return string.Join(";", parts);
}
