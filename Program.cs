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
var resolvedDatabaseMode = DatabaseConnectionModeResolver.Resolve(databaseConfig, builder.Environment.ContentRootPath);
builder.Services.AddSingleton(resolvedDatabaseMode);
var hermesOpenClawConfig = builder.Configuration
    .GetSection("HermesOpenClaw")
    .Get<HermesOpenClawConfig>() ?? new HermesOpenClawConfig();
builder.Services.AddSingleton(hermesOpenClawConfig);
builder.Services.Configure<DomainPresentationOptions>(
    builder.Configuration.GetSection(DomainPresentationOptions.SectionName));
builder.Services.Configure<TimeDisplayOptions>(
    builder.Configuration.GetSection(TimeDisplayOptions.SectionName));

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// SignalR
builder.Services.AddSignalR();

builder.Services.AddDbContextFactory<AppDbContext>(options =>
{
    if (resolvedDatabaseMode.Provider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase))
    {
        options.UseSqlServer(
            resolvedDatabaseMode.ConnectionString,
            sql => sql.EnableRetryOnFailure(
                maxRetryCount: 5,
                maxRetryDelay: TimeSpan.FromSeconds(10),
                errorNumbersToAdd: null));
    }
    else
    {
        options.UseSqlite(resolvedDatabaseMode.ConnectionString);
    }
});

// Core services
builder.Services.AddScoped<AgentService>();
builder.Services.AddScoped<TaskService>();
builder.Services.AddScoped<AgentMemoryCaptureService>();
builder.Services.AddScoped<LLMProviderService>();
builder.Services.AddScoped<TemplateService>();
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
builder.Services.AddSingleton<DomainPresentationService>();
builder.Services.AddSingleton<TimeDisplayService>();

builder.Services.AddHttpClient();

var app = builder.Build();

if (!string.IsNullOrWhiteSpace(resolvedDatabaseMode.StartupMessage))
{
    Console.WriteLine($"[Database] {resolvedDatabaseMode.StartupMessage}");
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
