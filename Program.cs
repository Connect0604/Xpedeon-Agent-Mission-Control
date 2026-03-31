using Microsoft.EntityFrameworkCore;
using XpedeonAgentMissionControl.Data;
using XpedeonAgentMissionControl.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Database
var dbPath = Path.Combine(builder.Environment.ContentRootPath, "xpedeon.db");
builder.Services.AddDbContextFactory<AppDbContext>(options =>
    options.UseSqlite($"Data Source={dbPath}"));

// Services
builder.Services.AddScoped<AgentService>();
builder.Services.AddScoped<TaskService>();
builder.Services.AddScoped<LLMProviderService>();
builder.Services.AddScoped<TemplateService>();
builder.Services.AddScoped<SwarmService>();
builder.Services.AddScoped<MemoryService>();
builder.Services.AddScoped<LLMExecutionService>();
builder.Services.AddSingleton<MockDataService>(); // kept for sim metrics until fully replaced

builder.Services.AddHttpClient();

var app = builder.Build();

// Apply migrations and seed on startup
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
    using var ctx = db.CreateDbContext();
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

app.Run();
