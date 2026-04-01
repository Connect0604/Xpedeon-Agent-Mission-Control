using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using XpedeonAgentMissionControl.Models;

namespace XpedeonAgentMissionControl.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Agent> Agents => Set<Agent>();
    public DbSet<AgentTask> Tasks => Set<AgentTask>();
    public DbSet<LogEntry> Logs => Set<LogEntry>();
    public DbSet<LLMProvider> LLMProviders => Set<LLMProvider>();
    public DbSet<Swarm> Swarms => Set<Swarm>();
    public DbSet<AgentTool> AgentTools => Set<AgentTool>();
    public DbSet<MCPServer> MCPServers => Set<MCPServer>();
    public DbSet<AgentMCPServer> AgentMCPServers => Set<AgentMCPServer>();
    public DbSet<AgentMemory> AgentMemories => Set<AgentMemory>();
    public DbSet<TaskFeedback> TaskFeedbacks => Set<TaskFeedback>();
    public DbSet<PromptHistory> PromptHistories => Set<PromptHistory>();
    public DbSet<AgentTemplate> AgentTemplates => Set<AgentTemplate>();
    public DbSet<AgentSchedule> AgentSchedules => Set<AgentSchedule>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Agent
        modelBuilder.Entity<Agent>(e =>
        {
            e.HasKey(a => a.Id);
            e.Property(a => a.CpuHistory).HasConversion(
                new ValueConverter<List<double>, string>(
                    v => string.Join(',', v),
                    v => v.Split(',', StringSplitOptions.RemoveEmptyEntries)
                          .Select(x => double.TryParse(x, out var d) ? d : 0.0)
                          .ToList()
                )
            );
            e.Property(a => a.TotalCostUSD).HasColumnType("decimal(18,6)");
            e.HasOne(a => a.LLMProvider).WithMany(p => p.Agents)
             .HasForeignKey(a => a.LLMProviderId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(a => a.Swarm).WithMany(s => s.Agents)
             .HasForeignKey(a => a.SwarmId).OnDelete(DeleteBehavior.SetNull);
            e.HasMany(a => a.Tools).WithOne(t => t.Agent)
             .HasForeignKey(t => t.AgentId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(a => a.Memories).WithOne(m => m.Agent)
             .HasForeignKey(m => m.AgentId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(a => a.PromptHistory).WithOne(p => p.Agent)
             .HasForeignKey(p => p.AgentId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(a => a.Schedule).WithOne(s => s.Agent)
             .HasForeignKey<AgentSchedule>(s => s.AgentId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(a => a.ParentAgent).WithMany(a => a.ChildAgents)
             .HasForeignKey(a => a.ParentAgentId).OnDelete(DeleteBehavior.SetNull);
        });

        // AgentTask
        modelBuilder.Entity<AgentTask>(e =>
        {
            e.HasKey(t => t.Id);
            e.Property(t => t.CostUSD).HasColumnType("decimal(18,6)");
            e.HasOne(t => t.Agent).WithMany(a => a.Tasks)
             .HasForeignKey(t => t.AgentId).OnDelete(DeleteBehavior.Cascade);
        });

        // LogEntry
        modelBuilder.Entity<LogEntry>(e =>
        {
            e.HasKey(l => l.Id);
            e.HasOne(l => l.Agent).WithMany()
             .HasForeignKey(l => l.AgentId).OnDelete(DeleteBehavior.SetNull);
        });

        // LLMProvider
        modelBuilder.Entity<LLMProvider>(e =>
        {
            e.HasKey(p => p.Id);
        });

        // Swarm
        modelBuilder.Entity<Swarm>(e =>
        {
            e.HasKey(s => s.Id);
        });

        // AgentTool
        modelBuilder.Entity<AgentTool>(e =>
        {
            e.HasKey(t => t.Id);
        });

        // MCPServer
        modelBuilder.Entity<MCPServer>(e =>
        {
            e.HasKey(s => s.Id);
        });

        // AgentMCPServer (composite key join table)
        modelBuilder.Entity<AgentMCPServer>(e =>
        {
            e.HasKey(am => new { am.AgentId, am.MCPServerId });
            e.HasOne(am => am.Agent).WithMany(a => a.MCPServers)
             .HasForeignKey(am => am.AgentId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(am => am.MCPServer).WithMany(s => s.AgentMCPServers)
             .HasForeignKey(am => am.MCPServerId).OnDelete(DeleteBehavior.Cascade);
        });

        // AgentMemory
        modelBuilder.Entity<AgentMemory>(e =>
        {
            e.HasKey(m => m.Id);
        });

        // TaskFeedback
        modelBuilder.Entity<TaskFeedback>(e =>
        {
            e.HasKey(f => f.Id);
        });

        // PromptHistory
        modelBuilder.Entity<PromptHistory>(e =>
        {
            e.HasKey(p => p.Id);
        });

        // AgentTemplate
        modelBuilder.Entity<AgentTemplate>(e =>
        {
            e.HasKey(t => t.Id);
        });

        // AgentSchedule
        modelBuilder.Entity<AgentSchedule>(e =>
        {
            e.HasKey(s => s.Id);
            e.HasIndex(s => s.AgentId).IsUnique();
        });

        // Seed Xpedeon built-in templates
        modelBuilder.Entity<AgentTemplate>().HasData(
            new AgentTemplate
            {
                Id = "tpl-001",
                Name = "ERP Sync Agent",
                Description = "Syncs project, PO, and vendor data between field systems and ERP. Detects mismatches and resolves conflicts.",
                Category = TemplateCategory.Xpedeon,
                DefaultType = AgentType.DataSync,
                SystemPrompt = "You are an ERP Sync Agent for Xpedeon. Your job is to synchronize data between field operations and the central ERP system. You must: 1) Identify records that are out of sync, 2) Resolve conflicts using the ERP as source of truth unless field data is newer, 3) Log all changes with reasons, 4) Flag records that require human review due to data quality issues. Always report counts of synced, skipped, and failed records.",
                RecommendedModel = "claude-sonnet-4-6",
                IsXpedeonBuiltIn = true,
                IconClass = "bi-arrow-left-right",
                Tags = "erp,sync,data,xpedeon",
                CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            },
            new AgentTemplate
            {
                Id = "tpl-002",
                Name = "Invoice Validator",
                Description = "Validates and enriches invoice records. Flags anomalies, duplicates, and missing fields.",
                Category = TemplateCategory.Xpedeon,
                DefaultType = AgentType.Processing,
                SystemPrompt = "You are an Invoice Validation Agent for Xpedeon. Your job is to validate incoming invoice records and ensure data quality. For each invoice batch you must: 1) Check for required fields (vendor ID, amount, date, PO reference), 2) Detect duplicates by cross-referencing invoice numbers, 3) Flag amount anomalies that deviate >20% from historical average, 4) Enrich missing GL codes based on vendor category, 5) Produce a validation report with pass/fail counts and reasons for each failure.",
                RecommendedModel = "claude-sonnet-4-6",
                IsXpedeonBuiltIn = true,
                IconClass = "bi-receipt-cutoff",
                Tags = "invoice,validation,finance,xpedeon",
                CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            },
            new AgentTemplate
            {
                Id = "tpl-003",
                Name = "Report Generator",
                Description = "Generates P&L, KPI, variance, and budget reports from raw operational data.",
                Category = TemplateCategory.Xpedeon,
                DefaultType = AgentType.Reporting,
                SystemPrompt = "You are a Report Generation Agent for Xpedeon. Your job is to generate structured business reports from raw data. When given data, you must: 1) Calculate key metrics (totals, averages, variance vs budget), 2) Identify top 5 cost drivers and revenue contributors, 3) Flag any metric that deviates >10% from the previous period, 4) Format output as a structured report with executive summary, detailed breakdown, and recommendations. Always include period-over-period comparisons.",
                RecommendedModel = "claude-sonnet-4-6",
                IsXpedeonBuiltIn = true,
                IconClass = "bi-file-earmark-bar-graph",
                Tags = "report,kpi,finance,analytics,xpedeon",
                CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            },
            new AgentTemplate
            {
                Id = "tpl-004",
                Name = "Compliance Checker",
                Description = "Audits transactions against regulatory rules and internal policies. Flags violations.",
                Category = TemplateCategory.Xpedeon,
                DefaultType = AgentType.Processing,
                SystemPrompt = "You are a Compliance Audit Agent for Xpedeon. Your job is to audit financial transactions and operational records against regulatory requirements and internal policies. For each audit run: 1) Check transactions against applicable rules (approval thresholds, vendor eligibility, budget limits), 2) Identify policy violations with severity levels (Critical, High, Medium, Low), 3) Verify audit trail completeness, 4) Generate a compliance report with violation counts, risk score, and remediation recommendations. Never suppress a finding — report everything.",
                RecommendedModel = "claude-sonnet-4-6",
                IsXpedeonBuiltIn = true,
                IconClass = "bi-shield-check",
                Tags = "compliance,audit,risk,xpedeon",
                CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            },
            new AgentTemplate
            {
                Id = "tpl-005",
                Name = "Notification Dispatcher",
                Description = "Monitors thresholds and dispatches targeted alerts to stakeholders.",
                Category = TemplateCategory.Xpedeon,
                DefaultType = AgentType.Notification,
                SystemPrompt = "You are a Notification Dispatch Agent for Xpedeon. Your job is to monitor operational metrics and dispatch targeted notifications to the right stakeholders. You must: 1) Evaluate incoming metrics against defined thresholds, 2) Determine the correct recipient based on the alert type and severity, 3) Compose clear, actionable notification messages with context and suggested actions, 4) Avoid duplicate alerts — check if a similar alert was sent in the last 4 hours, 5) Log all dispatched notifications with delivery status.",
                RecommendedModel = "claude-haiku-4-5-20251001",
                IsXpedeonBuiltIn = true,
                IconClass = "bi-bell",
                Tags = "notification,alert,stakeholder,xpedeon",
                CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            },
            new AgentTemplate
            {
                Id = "tpl-006",
                Name = "Field Sync Bot",
                Description = "Pulls field operations data and reconciles with central records.",
                Category = TemplateCategory.Xpedeon,
                DefaultType = AgentType.DataSync,
                SystemPrompt = "You are a Field Sync Bot for Xpedeon. Your job is to pull data from field operations systems and reconcile it with central project records. For each sync run: 1) Pull latest field data (attendance, progress, materials used), 2) Match field records to central project codes, 3) Flag unmatched records for manual review, 4) Calculate field vs planned progress variance, 5) Update central records with verified field data. Report sync summary with matched, unmatched, and updated counts.",
                RecommendedModel = "claude-haiku-4-5-20251001",
                IsXpedeonBuiltIn = true,
                IconClass = "bi-geo-alt",
                Tags = "field,sync,operations,xpedeon",
                CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            },
            new AgentTemplate
            {
                Id = "tpl-007",
                Name = "Cost Analyser",
                Description = "Analyses cost variance vs budget and identifies overspend patterns.",
                Category = TemplateCategory.Xpedeon,
                DefaultType = AgentType.Processing,
                SystemPrompt = "You are a Cost Analysis Agent for Xpedeon. Your job is to analyse project costs against budgets and identify patterns. For each analysis run: 1) Calculate budget utilisation per cost code and WBS element, 2) Identify overspend items (actual > budget by >5%), 3) Project end-of-project cost based on current burn rate, 4) Highlight top 3 cost saving opportunities, 5) Compare against similar completed projects for benchmarking. Produce a concise cost health report with RAG (Red/Amber/Green) status per category.",
                RecommendedModel = "claude-sonnet-4-6",
                IsXpedeonBuiltIn = true,
                IconClass = "bi-graph-down-arrow",
                Tags = "cost,budget,analysis,xpedeon",
                CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            }
        );
    }
}
