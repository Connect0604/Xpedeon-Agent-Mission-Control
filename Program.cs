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

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// SignalR
builder.Services.AddSignalR();

builder.Services.AddDbContextFactory<AppDbContext>(options =>
{
    var provider = (databaseConfig.Provider ?? "SQLite").Trim();
    if (provider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase))
    {
        options.UseSqlServer(BuildSqlServerConnectionString(databaseConfig));
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
builder.Services.AddScoped<SwarmService>();
builder.Services.AddScoped<SelfLearningService>();
builder.Services.AddScoped<DynamicSpawnService>();
builder.Services.AddScoped<MCPService>();
builder.Services.AddHostedService<AgentSchedulerService>();
builder.Services.AddScoped<MemoryService>();
builder.Services.AddScoped<LLMExecutionService>();
builder.Services.AddScoped<LogService>();
builder.Services.AddSingleton<RealtimeService>();
builder.Services.AddSingleton<MockDataService>(); // kept for sim feed

builder.Services.AddHttpClient();

var app = builder.Build();

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
        "MultipleActiveResultSets=True"
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
