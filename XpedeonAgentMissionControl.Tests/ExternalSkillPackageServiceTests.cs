using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using XpedeonAgentMissionControl.Configuration;
using XpedeonAgentMissionControl.Data;
using XpedeonAgentMissionControl.Models;
using XpedeonAgentMissionControl.Services;

namespace XpedeonAgentMissionControl.Tests;

public class ExternalSkillPackageServiceTests
{
    [Fact]
    public async Task ImportFromGitHubAsync_ImportsManagedMcpPackage()
    {
        await using var harness = await TestHarness.CreateAsync(new Dictionary<string, string>
        {
            [ManifestRawUrl] = ManifestJson
        });

        var package = await harness.Service.ImportFromGitHubAsync(ManifestBlobUrl);

        Assert.Equal(ExternalSkillPackageKind.ManagedMcp, package.Kind);
        Assert.Equal("filesystem-tools", package.PackageKey);
        Assert.Equal("packages/filesystem/xpedeon-skill.json", package.ManifestPath);
        Assert.Equal(ExternalSkillPackageApprovalStatus.PendingReview, package.ApprovalStatus);
    }

    [Fact]
    public async Task ImportFromGitHubAsync_ImportsSkillRepoFromSkillMarkdown()
    {
        await using var harness = await TestHarness.CreateAsync(new Dictionary<string, string>
        {
            [SkillRawUrl] = SkillMarkdown
        });

        var package = await harness.Service.ImportFromGitHubAsync(SkillBlobUrl);

        await using var db = harness.CreateDbContext();
        var skill = await db.SkillDefinitions.SingleAsync(s => s.ExternalSkillPackageId == package.Id);

        Assert.Equal(ExternalSkillPackageKind.AgentSkill, package.Kind);
        Assert.Equal("humanizer", package.PackageKey);
        Assert.Equal("SKILL.md", package.ManifestPath);
        Assert.Equal(ExternalSkillPackageInstallStatus.Draft, package.InstallStatus);
        Assert.False(package.IsEnabled);
        Assert.Equal("humanizer", skill.Name);
        Assert.False(skill.IsEnabled);
        Assert.Contains("Transforms rough writing", skill.Description);
    }

    [Fact]
    public async Task ApprovePackageAsync_DoesNotRequireProvisioning_ForSkillRepo()
    {
        await using var harness = await TestHarness.CreateAsync(new Dictionary<string, string>
        {
            [SkillRawUrl] = SkillMarkdown
        });

        var imported = await harness.Service.ImportFromGitHubAsync(SkillBlobUrl);
        var approved = await harness.Service.ApprovePackageAsync(imported.Id);

        Assert.Equal(ExternalSkillPackageKind.AgentSkill, approved.Kind);
        Assert.Equal(ExternalSkillPackageApprovalStatus.Approved, approved.ApprovalStatus);
        Assert.Equal(ExternalSkillPackageInstallStatus.Healthy, approved.InstallStatus);
    }

    [Fact]
    public async Task ApprovePackageAsync_EnablesLinkedSkill_ForSkillRepo()
    {
        await using var harness = await TestHarness.CreateAsync(new Dictionary<string, string>
        {
            [SkillRawUrl] = SkillMarkdown
        });

        var imported = await harness.Service.ImportFromGitHubAsync(SkillBlobUrl);
        await harness.Service.ApprovePackageAsync(imported.Id);

        await using var db = harness.CreateDbContext();
        var skill = await db.SkillDefinitions.SingleAsync(s => s.ExternalSkillPackageId == imported.Id);

        Assert.True(skill.IsEnabled);
    }

    [Fact]
    public async Task ProvisionManagedMcpServerAsync_RejectsSkillOnlyPackage()
    {
        await using var harness = await TestHarness.CreateAsync(new Dictionary<string, string>
        {
            [SkillRawUrl] = SkillMarkdown
        });

        var imported = await harness.Service.ImportFromGitHubAsync(SkillBlobUrl);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Service.ProvisionManagedMcpServerAsync(imported.Id));

        Assert.Contains("managed MCP", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckHealthAsync_EnablesManagedMcp_WhenDeclaredToolsMatch()
    {
        await using var harness = await TestHarness.CreateAsync(new Dictionary<string, string>
        {
            [ManifestRawUrl] = ManifestJson
        }, new FakeMcpProbe
        {
            TestConnectionResult = new MCPConnectionTestResult { Success = true, ToolCount = 2 },
            ToolDiscoveryResult = new MCPToolDiscoveryResult
            {
                Success = true,
                Tools = new List<MCPToolInfo> { new() { Name = "read_file" }, new() { Name = "write_file" } }
            }
        });

        var imported = await harness.Service.ImportFromGitHubAsync(ManifestBlobUrl);
        await harness.Service.ApprovePackageAsync(imported.Id);
        await harness.Service.ProvisionManagedMcpServerAsync(imported.Id);
        var health = await harness.Service.CheckHealthAsync(imported.Id);

        Assert.True(health.Success);
    }

    [Fact]
    public async Task CheckHealthAsync_EnablesLinkedSkills_ForManagedMcpPackage()
    {
        await using var harness = await TestHarness.CreateAsync(new Dictionary<string, string>
        {
            [ManifestRawUrl] = ManifestJson
        }, new FakeMcpProbe
        {
            TestConnectionResult = new MCPConnectionTestResult { Success = true, ToolCount = 2 },
            ToolDiscoveryResult = new MCPToolDiscoveryResult
            {
                Success = true,
                Tools = new List<MCPToolInfo> { new() { Name = "read_file" }, new() { Name = "write_file" } }
            }
        });

        var imported = await harness.Service.ImportFromGitHubAsync(ManifestBlobUrl);
        await harness.Service.ApprovePackageAsync(imported.Id);
        await harness.Service.ProvisionManagedMcpServerAsync(imported.Id);
        await harness.Service.CheckHealthAsync(imported.Id);

        await using var db = harness.CreateDbContext();
        var skill = await db.SkillDefinitions.SingleAsync(s => s.ExternalSkillPackageId == imported.Id);

        Assert.True(skill.IsEnabled);
    }

    private const string ManifestBlobUrl = "https://github.com/acme/tools/blob/main/packages/filesystem/xpedeon-skill.json";
    private const string ManifestRawUrl = "https://raw.githubusercontent.com/acme/tools/main/packages/filesystem/xpedeon-skill.json";
    private const string SkillBlobUrl = "https://github.com/blader/humanizer/blob/main/SKILL.md";
    private const string SkillRawUrl = "https://raw.githubusercontent.com/blader/humanizer/main/SKILL.md";

    private const string ManifestJson = """
    {
      "packageId": "filesystem-tools",
      "name": "Filesystem Tools",
      "version": "1.0.0",
      "description": "Managed filesystem MCP package.",
      "runtime": {
        "transportType": "Stdio",
        "endpoint": "npx -y @modelcontextprotocol/server-filesystem C:/workspace"
      },
      "tools": [
        { "name": "read_file", "description": "Read a file." },
        { "name": "write_file", "description": "Write a file." }
      ],
      "requiredSecrets": [ "GITHUB_TOKEN" ],
      "permissionHints": [ "Reads and writes local workspace files." ],
      "defaultSkills": [
        {
          "name": "Filesystem Operations",
          "category": "General",
          "description": "Use filesystem tools carefully.",
          "promptSnippet": "Prefer read_file before write_file.",
          "allowedMcpToolNames": [ "read_file", "write_file" ],
          "maxToolCalls": 3,
          "requiresApproval": false
        }
      ]
    }
    """;

    private const string SkillMarkdown = """
    ---
    name: humanizer
    description: Transforms rough writing into natural, concise, human-sounding prose.
    ---

    # Humanizer

    Use this skill when the user wants content to sound more human, polished, and concise.
    """;

    private sealed class TestHarness : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly TestDbContextFactory _factory;
        private TestHarness(SqliteConnection connection, TestDbContextFactory factory, ExternalSkillPackageService service)
        {
            _connection = connection;
            _factory = factory;
            Service = service;
        }

        public ExternalSkillPackageService Service { get; }

        public static async Task<TestHarness> CreateAsync(Dictionary<string, string> responses, FakeMcpProbe? probe = null)
        {
            var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
            var factory = new TestDbContextFactory(options);
            await using (var db = factory.CreateDbContext())
            {
                await db.Database.EnsureCreatedAsync();
            }

            var service = new ExternalSkillPackageService(factory, new StubHttpClientFactory(responses), probe ?? new FakeMcpProbe());
            return new TestHarness(connection, factory, service);
        }

        public AppDbContext CreateDbContext() => _factory.CreateDbContext();

        public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
    }

    private sealed class TestDbContextFactory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options;
        public TestDbContextFactory(DbContextOptions<AppDbContext> options) => _options = options;
        public AppDbContext CreateDbContext() => new(_options, new DatabaseConfig());
        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly Dictionary<string, string> _responses;
        public StubHttpClientFactory(Dictionary<string, string> responses) => _responses = responses;
        public HttpClient CreateClient(string name) => new(new StubHttpMessageHandler(_responses));
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _responses;
        public StubHttpMessageHandler(Dictionary<string, string> responses) => _responses = responses;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var key = request.RequestUri!.ToString();
            if (_responses.TryGetValue(key, out var content))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(content, Encoding.UTF8, key.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? "application/json" : "text/plain")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("missing", Encoding.UTF8, "text/plain")
            });
        }
    }

    private sealed class FakeMcpProbe : IMcpConnectionProbe
    {
        public MCPConnectionTestResult TestConnectionResult { get; set; } = new() { Success = true };
        public MCPToolDiscoveryResult ToolDiscoveryResult { get; set; } = new() { Success = true };
        public Task<MCPConnectionTestResult> TestConnectionAsync(MCPServer server, CancellationToken cancellationToken = default) => Task.FromResult(TestConnectionResult);
        public Task<MCPToolDiscoveryResult> ListToolsAsync(MCPServer server, CancellationToken cancellationToken = default) => Task.FromResult(ToolDiscoveryResult);
    }
}
