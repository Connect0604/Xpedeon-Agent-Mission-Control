using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.EntityFrameworkCore.Metadata;
using System.Text.RegularExpressions;
using XpedeonAgentMissionControl.Configuration;
using XpedeonAgentMissionControl.Models;

namespace XpedeonAgentMissionControl.Data;

public class AppDbContext : DbContext
{
    private readonly DatabaseConfig _databaseConfig;

    public AppDbContext(DbContextOptions<AppDbContext> options, DatabaseConfig databaseConfig) : base(options)
    {
        _databaseConfig = databaseConfig;
    }

    public DbSet<Agent> Agents => Set<Agent>();
    public DbSet<AgentTask> Tasks => Set<AgentTask>();
    public DbSet<LogEntry> Logs => Set<LogEntry>();
    public DbSet<LLMProvider> LLMProviders => Set<LLMProvider>();
    public DbSet<Swarm> Swarms => Set<Swarm>();
    public DbSet<AgentTool> AgentTools => Set<AgentTool>();
    public DbSet<SkillDefinition> SkillDefinitions => Set<SkillDefinition>();
    public DbSet<AgentSkillAssignment> AgentSkillAssignments => Set<AgentSkillAssignment>();
    public DbSet<ExternalSkillPackage> ExternalSkillPackages => Set<ExternalSkillPackage>();
    public DbSet<ExternalSkillPackageInstall> ExternalSkillPackageInstalls => Set<ExternalSkillPackageInstall>();
    public DbSet<MCPServer> MCPServers => Set<MCPServer>();
    public DbSet<AgentMCPServer> AgentMCPServers => Set<AgentMCPServer>();
    public DbSet<AgentMemory> AgentMemories => Set<AgentMemory>();
    public DbSet<TaskFeedback> TaskFeedbacks => Set<TaskFeedback>();
    public DbSet<PromptHistory> PromptHistories => Set<PromptHistory>();
    public DbSet<TaskExecutionEvent> TaskExecutionEvents => Set<TaskExecutionEvent>();
    public DbSet<AgentTemplate> AgentTemplates => Set<AgentTemplate>();
    public DbSet<AgentSchedule> AgentSchedules => Set<AgentSchedule>();
    public DbSet<EvaluationScenario> EvaluationScenarios => Set<EvaluationScenario>();
    public DbSet<EvaluationRun> EvaluationRuns => Set<EvaluationRun>();
    public DbSet<LocalCapability> LocalCapabilities => Set<LocalCapability>();
    public DbSet<AgentWorkflow> Workflows => Set<AgentWorkflow>();
    public DbSet<AgentWorkflowStep> WorkflowSteps => Set<AgentWorkflowStep>();
    public DbSet<WorkflowRun> WorkflowRuns => Set<WorkflowRun>();
    public DbSet<WorkflowStepRun> WorkflowStepRuns => Set<WorkflowStepRun>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        if (Database.ProviderName?.Contains("SqlServer", StringComparison.OrdinalIgnoreCase) == true &&
            !string.IsNullOrWhiteSpace(_databaseConfig.Schema))
        {
            modelBuilder.HasDefaultSchema(_databaseConfig.Schema);
        }

        // Agent
        modelBuilder.Entity<Agent>(e =>
        {
            e.HasKey(a => a.Id);
            e.Property(a => a.LocalAutomationEnabled);
            e.Property(a => a.AllowPowerShellScripts);
            e.Property(a => a.AllowDestructiveActions);
            e.Property(a => a.LocalAutomationApprovalMode);
            e.Property(a => a.AllowedLocalRootsJson);
            e.Property(a => a.CpuHistory).HasConversion(
                new ValueConverter<List<double>, string>(
                    v => string.Join(',', v),
                    v => v.Split(',', StringSplitOptions.RemoveEmptyEntries)
                          .Select(x => TryParseDouble(x))
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
            e.HasMany(a => a.Skills).WithOne(s => s.Agent)
             .HasForeignKey(s => s.AgentId).OnDelete(DeleteBehavior.Cascade);
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
            e.Property(t => t.LocalCapabilityId);
            e.Property(t => t.LocalCapabilityDraftJson);
            e.Property(t => t.LocalCapabilityExecutionJson);
            e.Property(t => t.LocalActionPlanJson);
            e.Property(t => t.LocalActionResultJson);
            e.Property(t => t.TouchedPathsJson);
            e.Property(t => t.RequiresElevatedApproval);
            e.Property(t => t.CostUSD).HasColumnType("decimal(18,6)");
            e.HasOne<LocalCapability>()
             .WithMany()
             .HasForeignKey(t => t.LocalCapabilityId)
             .OnDelete(DeleteBehavior.SetNull);
            e.HasOne(t => t.Agent).WithMany(a => a.Tasks)
             .HasForeignKey(t => t.AgentId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(t => t.ExecutionEvents).WithOne(te => te.Task)
             .HasForeignKey(te => te.TaskId).OnDelete(DeleteBehavior.Cascade);
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

        // SkillDefinition
        modelBuilder.Entity<SkillDefinition>(e =>
        {
            e.HasKey(s => s.Id);
            e.HasOne<LLMProvider>()
             .WithMany()
             .HasForeignKey(s => s.PreferredProviderId)
             .OnDelete(DeleteBehavior.SetNull);
            e.HasOne(s => s.ExternalSkillPackage)
             .WithMany()
             .HasForeignKey(s => s.ExternalSkillPackageId)
             .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<ExternalSkillPackage>(e =>
        {
            e.HasKey(p => p.Id);
            e.HasIndex(p => p.PackageKey);
        });

        modelBuilder.Entity<ExternalSkillPackageInstall>(e =>
        {
            e.HasKey(i => i.Id);
            e.HasOne(i => i.ExternalSkillPackage)
             .WithMany(p => p.Installs)
             .HasForeignKey(i => i.ExternalSkillPackageId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(i => i.ProvisionedMcpServer)
             .WithMany()
             .HasForeignKey(i => i.ProvisionedMcpServerId)
             .OnDelete(DeleteBehavior.SetNull);
        });

        // AgentSkillAssignment
        modelBuilder.Entity<AgentSkillAssignment>(e =>
        {
            e.HasKey(s => new { s.AgentId, s.SkillDefinitionId });
            e.HasOne(s => s.Agent).WithMany(a => a.Skills)
             .HasForeignKey(s => s.AgentId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(s => s.SkillDefinition).WithMany(d => d.AgentAssignments)
             .HasForeignKey(s => s.SkillDefinitionId).OnDelete(DeleteBehavior.Cascade);
        });

        // MCPServer
        modelBuilder.Entity<MCPServer>(e =>
        {
            e.HasKey(s => s.Id);
            e.HasOne(s => s.ExternalSkillPackage)
             .WithMany()
             .HasForeignKey(s => s.ExternalSkillPackageId)
             .OnDelete(DeleteBehavior.SetNull);
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

        // TaskExecutionEvent
        modelBuilder.Entity<TaskExecutionEvent>(e =>
        {
            e.HasKey(te => te.Id);
            e.HasIndex(te => new { te.TaskId, te.CreatedAt });
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

        // EvaluationScenario
        modelBuilder.Entity<EvaluationScenario>(e =>
        {
            e.HasKey(s => s.Id);
            e.HasOne(s => s.BaseAgent).WithMany()
             .HasForeignKey(s => s.BaseAgentId).OnDelete(DeleteBehavior.SetNull);
        });

        // EvaluationRun
        modelBuilder.Entity<EvaluationRun>(e =>
        {
            e.HasKey(r => r.Id);
            e.Property(r => r.CostUSD).HasColumnType("decimal(18,6)");
            e.HasOne(r => r.Scenario).WithMany(s => s.Runs)
             .HasForeignKey(r => r.EvaluationScenarioId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<LocalCapability>(e =>
        {
            e.HasKey(c => c.Id);
            e.Property(c => c.Id).HasMaxLength(64);
            e.Property(c => c.Name).IsRequired().HasMaxLength(255);
            e.Property(c => c.DisplayName).IsRequired().HasMaxLength(255);
            e.Property(c => c.Description).IsRequired();
            e.Property(c => c.HandlerKey).HasMaxLength(255);
            e.Property(c => c.ScriptPath).HasMaxLength(1024);
            e.Property(c => c.InputSchemaJson).IsRequired();
            e.Property(c => c.OutputSchemaJson).IsRequired();
        });

        modelBuilder.Entity<AgentWorkflow>(e =>
        {
            e.HasKey(w => w.Id);
            e.HasMany(w => w.Steps).WithOne(s => s.Workflow)
                .HasForeignKey(s => s.WorkflowId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(w => w.Runs).WithOne(r => r.Workflow)
                .HasForeignKey(r => r.WorkflowId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AgentWorkflowStep>(e =>
        {
            e.HasKey(s => s.Id);
            e.HasIndex(s => new { s.WorkflowId, s.StepOrder }).IsUnique();
            e.HasOne(s => s.Agent).WithMany()
                .HasForeignKey(s => s.AgentId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(s => s.Swarm).WithMany()
                .HasForeignKey(s => s.SwarmId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<WorkflowRun>(e =>
        {
            e.HasKey(r => r.Id);
            e.HasIndex(r => new { r.WorkflowId, r.CreatedAt });
            e.HasMany(r => r.StepRuns).WithOne(sr => sr.WorkflowRun)
                .HasForeignKey(sr => sr.WorkflowRunId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WorkflowStepRun>(e =>
        {
            e.HasKey(sr => sr.Id);
            e.HasIndex(sr => new { sr.WorkflowRunId, sr.StepOrder });
            e.HasOne(sr => sr.WorkflowStep).WithMany(s => s.StepRuns)
                .HasForeignKey(sr => sr.WorkflowStepId).OnDelete(DeleteBehavior.Restrict);
        });

        if (Database.ProviderName?.Contains("SqlServer", StringComparison.OrdinalIgnoreCase) == true)
        {
            ApplySqlServerObjectMappings(modelBuilder);
        }

        modelBuilder.Entity<SkillDefinition>().HasData(
            new SkillDefinition
            {
                Id = "skill-001",
                Name = "DB Schema Lookup",
                Category = SkillCategory.Database,
                Description = "Retrieve table definitions, indexes, and stored procedure shapes through approved MCP database tools.",
                PromptSnippet = "Use this skill for database metadata requests. Prefer exact object names and return concise structured schema summaries.",
                AllowedMcpToolNamesJson = "[\"xpedeon-database-tool\",\"xpedeon-database-legacy-tool\"]",
                PreferredModelName = "Hermes 3 / tool-calling model",
                MaxToolCalls = 4,
                RequiresApproval = false,
                IsBuiltIn = true,
                IsEnabled = true,
                CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            },
            new SkillDefinition
            {
                Id = "skill-002",
                Name = "Invoice Validation",
                Category = SkillCategory.Finance,
                Description = "Validate invoice records, detect anomalies, and summarize issues before posting or approval.",
                PromptSnippet = "Use this skill when validating invoices. Highlight missing fields, duplicates, suspicious amounts, and approval blockers before giving a recommendation.",
                AllowedMcpToolNamesJson = "[]",
                PreferredModelName = "Hermes 3 / high-precision validator",
                MaxToolCalls = 2,
                RequiresApproval = true,
                IsBuiltIn = true,
                IsEnabled = true,
                CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            },
            new SkillDefinition
            {
                Id = "skill-003",
                Name = "ERP Sync Audit",
                Category = SkillCategory.Integration,
                Description = "Audit ERP sync tasks, compare system states, and call out mismatches with follow-up actions.",
                PromptSnippet = "Use this skill for reconciliation and sync audits. Focus on mismatches, source-of-truth decisions, and next-step actions.",
                AllowedMcpToolNamesJson = "[]",
                MaxToolCalls = 3,
                RequiresApproval = false,
                IsBuiltIn = true,
                IsEnabled = true,
                CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            },
            new SkillDefinition
            {
                Id = "skill-004",
                Name = "Cost Analysis",
                Category = SkillCategory.Reporting,
                Description = "Analyze budget variance, burn rates, and overspend patterns with actionable recommendations.",
                PromptSnippet = "Use this skill for project cost analysis. Summarize overspend drivers, likely risk areas, and recommended interventions in a business-friendly format.",
                AllowedMcpToolNamesJson = "[]",
                MaxToolCalls = 2,
                RequiresApproval = false,
                IsBuiltIn = true,
                IsEnabled = true,
                CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            }
        );

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

    private static double TryParseDouble(string x)
    {
        double.TryParse(x, out var d);
        return d;
    }

    private static void ApplySqlServerObjectMappings(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Agent>().ToTable("AGENTS");
        modelBuilder.Entity<AgentTask>().ToTable("AGENT_TASKS");
        modelBuilder.Entity<LogEntry>().ToTable("LOG_ENTRIES");
        modelBuilder.Entity<LLMProvider>().ToTable("LLM_PROVIDERS");
        modelBuilder.Entity<Swarm>().ToTable("SWARMS");
        modelBuilder.Entity<AgentTool>().ToTable("AGENT_TOOLS");
        modelBuilder.Entity<SkillDefinition>().ToTable("SKILL_DEFINITIONS");
        modelBuilder.Entity<AgentSkillAssignment>().ToTable("AGENT_SKILL_ASSIGNMENTS");
        modelBuilder.Entity<ExternalSkillPackage>().ToTable("EXTERNAL_SKILL_PACKAGES");
        modelBuilder.Entity<ExternalSkillPackageInstall>().ToTable("EXTERNAL_SKILL_PACKAGE_INSTALLS");
        modelBuilder.Entity<MCPServer>().ToTable("MCP_SERVERS");
        modelBuilder.Entity<AgentMCPServer>().ToTable("AGENT_MCP_SERVERS");
        modelBuilder.Entity<AgentMemory>().ToTable("AGENT_MEMORIES");
        modelBuilder.Entity<TaskFeedback>().ToTable("TASK_FEEDBACKS");
        modelBuilder.Entity<PromptHistory>().ToTable("PROMPT_HISTORIES");
        modelBuilder.Entity<TaskExecutionEvent>().ToTable("TASK_EXECUTION_EVENTS");
        modelBuilder.Entity<AgentTemplate>().ToTable("AGENT_TEMPLATES");
        modelBuilder.Entity<AgentSchedule>().ToTable("AGENT_SCHEDULES");
        modelBuilder.Entity<EvaluationScenario>().ToTable("EVALUATION_SCENARIOS");
        modelBuilder.Entity<EvaluationRun>().ToTable("EVALUATION_RUNS");
        modelBuilder.Entity<LocalCapability>().ToTable("LOCAL_CAPABILITIES");
        modelBuilder.Entity<AgentWorkflow>().ToTable("AGENT_WORKFLOWS");
        modelBuilder.Entity<AgentWorkflowStep>().ToTable("AGENT_WORKFLOW_STEPS");
        modelBuilder.Entity<WorkflowRun>().ToTable("WORKFLOW_RUNS");
        modelBuilder.Entity<WorkflowStepRun>().ToTable("WORKFLOW_STEP_RUNS");

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                property.SetColumnName(ToUpperSnakeCase(property.Name));
            }
        }

        modelBuilder.Entity<AgentMemory>().Property(m => m.Key).HasColumnName("MEMORY_KEY");
    }

    private static string ToUpperSnakeCase(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var withWordBreaks = Regex.Replace(value, "([a-z0-9])([A-Z])", "$1_$2");
        withWordBreaks = Regex.Replace(withWordBreaks, "([A-Z]+)([A-Z][a-z])", "$1_$2");
        return withWordBreaks.ToUpperInvariant();
    }
}
