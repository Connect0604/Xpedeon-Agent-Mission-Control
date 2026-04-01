using Microsoft.EntityFrameworkCore;
using XpedeonAgentMissionControl.Data;
using XpedeonAgentMissionControl.Hubs;
using XpedeonAgentMissionControl.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// SignalR
builder.Services.AddSignalR();

// Database
var dbPath = Path.Combine(builder.Environment.ContentRootPath, "xpedeon.db");
builder.Services.AddDbContextFactory<AppDbContext>(options =>
    options.UseSqlite($"Data Source={dbPath}"));

// Core services
builder.Services.AddScoped<AgentService>();
builder.Services.AddScoped<TaskService>();
builder.Services.AddScoped<LLMProviderService>();
builder.Services.AddScoped<TemplateService>();
builder.Services.AddScoped<SwarmService>();
builder.Services.AddScoped<SelfLearningService>();
builder.Services.AddScoped<DynamicSpawnService>();
builder.Services.AddHostedService<AgentSchedulerService>();
builder.Services.AddScoped<MemoryService>();
builder.Services.AddScoped<LLMExecutionService>();
builder.Services.AddScoped<LogService>();
builder.Services.AddScoped<RealtimeService>();
builder.Services.AddSingleton<MockDataService>(); // kept for sim feed

builder.Services.AddHttpClient();

var app = builder.Build();

// Ensure DB is up-to-date on startup (dev: drop+recreate to apply schema changes)
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
    using var ctx = db.CreateDbContext();
    ctx.Database.EnsureDeleted();
    ctx.Database.EnsureCreated();
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
