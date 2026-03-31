using XpedeonAgentMissionControl.Models;
using ModelLogLevel = XpedeonAgentMissionControl.Models.LogLevel;

namespace XpedeonAgentMissionControl.Services;

public class MockDataService : IDisposable
{
    private readonly List<Agent> _agents;
    private readonly List<AgentTask> _tasks;
    private readonly List<LogEntry> _logs;
    private readonly Timer _timer;
    private readonly Random _rng = new();
    private bool _disposed;

    public event Action? OnDataChanged;

    public MockDataService()
    {
        _agents = SeedAgents();
        _tasks  = SeedTasks();
        _logs   = SeedLogs();
        _timer  = new Timer(Tick, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2.5));
    }

    // ─── Read API ────────────────────────────────────────────────────────────

    public IReadOnlyList<Agent>    GetAgents()  => _agents.AsReadOnly();
    public Agent?                  GetAgent(string id) => _agents.FirstOrDefault(a => a.Id == id);
    public IReadOnlyList<AgentTask> GetTasks()  => _tasks.AsReadOnly();
    public IReadOnlyList<AgentTask> GetTasksForAgent(string agentId) =>
        _tasks.Where(t => t.AgentId == agentId).OrderByDescending(t => t.CreatedAt).ToList();
    public IReadOnlyList<LogEntry> GetLogs(int count = 100) =>
        _logs.Take(count).ToList();
    public IReadOnlyList<LogEntry> GetLogsForAgent(string agentId, int count = 50) =>
        _logs.Where(l => l.AgentId == agentId).Take(count).ToList();

    public DashboardSummary GetSummary()
    {
        var today = DateTime.UtcNow.Date;
        return new DashboardSummary
        {
            TotalAgents          = _agents.Count,
            ActiveAgents         = _agents.Count(a => a.Status == AgentStatus.Active),
            IdleAgents           = _agents.Count(a => a.Status == AgentStatus.Idle),
            WarningAgents        = _agents.Count(a => a.Status == AgentStatus.Warning),
            ErrorAgents          = _agents.Count(a => a.Status == AgentStatus.Error),
            OfflineAgents        = _agents.Count(a => a.Status == AgentStatus.Offline),
            TotalTasksToday      = _tasks.Count(t => t.CreatedAt.Date == today),
            RunningTasks         = _tasks.Count(t => t.Status == AgentTaskStatus.Running),
            QueuedTasks          = _tasks.Count(t => t.Status == AgentTaskStatus.Queued),
            CompletedTasksToday  = _tasks.Count(t => t.Status == AgentTaskStatus.Completed && t.CreatedAt.Date == today),
            FailedTasksToday     = _tasks.Count(t => t.Status == AgentTaskStatus.Failed && t.CreatedAt.Date == today),
            AverageSuccessRate   = _agents.Any() ? _agents.Average(a => a.SuccessRate) : 100,
            TotalCpuUsage        = _agents.Where(a => a.Status == AgentStatus.Active).Any()
                                      ? _agents.Where(a => a.Status == AgentStatus.Active).Average(a => a.CpuUsage)
                                      : 0,
        };
    }

    // ─── Simulation ──────────────────────────────────────────────────────────

    private void Tick(object? _)
    {
        lock (_agents)
        {
            UpdateAgentMetrics();
            AdvanceTasks();
            MaybeAddLog();
        }
        OnDataChanged?.Invoke();
    }

    private void UpdateAgentMetrics()
    {
        foreach (var agent in _agents)
        {
            if (agent.Status is AgentStatus.Offline) continue;

            agent.LastSeen = DateTime.UtcNow;

            if (agent.Status is AgentStatus.Active)
            {
                agent.CpuUsage = Clamp(agent.CpuUsage + (_rng.NextDouble() - 0.45) * 12, 3, 98);
                agent.MemoryUsageMB = Clamp(agent.MemoryUsageMB + (_rng.NextDouble() - 0.48) * 8, 60, agent.MemoryLimitMB * 0.95);

                // Maintain history (last 20 points)
                agent.CpuHistory.Add(Math.Round(agent.CpuUsage, 1));
                if (agent.CpuHistory.Count > 20) agent.CpuHistory.RemoveAt(0);
            }

            // Rare status transitions
            var roll = _rng.NextDouble();
            agent.Status = agent.Status switch
            {
                AgentStatus.Active  => roll < 0.02 ? AgentStatus.Warning : roll < 0.005 ? AgentStatus.Error : AgentStatus.Active,
                AgentStatus.Idle    => roll < 0.12 ? AgentStatus.Active  : AgentStatus.Idle,
                AgentStatus.Warning => roll < 0.15 ? AgentStatus.Error   : roll < 0.35 ? AgentStatus.Active : AgentStatus.Warning,
                AgentStatus.Error   => roll < 0.08 ? AgentStatus.Offline : roll < 0.20 ? AgentStatus.Warning : AgentStatus.Error,
                AgentStatus.Offline => roll < 0.05 ? AgentStatus.Idle    : AgentStatus.Offline,
                _ => agent.Status
            };
        }
    }

    private void AdvanceTasks()
    {
        var running = _tasks.Where(t => t.Status == AgentTaskStatus.Running).ToList();
        foreach (var task in running)
        {
            task.Progress = Math.Min(task.Progress + _rng.Next(2, 10), 100);
            task.RecordsProcessed = (long)(task.RecordsTotal * task.Progress / 100.0);

            if (task.Progress >= 100)
            {
                var succeed = _rng.NextDouble() > 0.06;
                task.Status = succeed ? AgentTaskStatus.Completed : AgentTaskStatus.Failed;
                task.CompletedAt = DateTime.UtcNow;
                if (!succeed) task.ErrorMessage = RandomErrorMessage();

                var agent = _agents.FirstOrDefault(a => a.Id == task.AgentId);
                if (agent != null)
                {
                    if (succeed) agent.TasksCompleted++;
                    else agent.TasksFailed++;
                }

                AddLog(task.AgentId, task.AgentName,
                    succeed ? $"Task '{task.Name}' completed — {task.RecordsTotal:N0} records processed"
                            : $"Task '{task.Name}' failed: {task.ErrorMessage}",
                    succeed ? ModelLogLevel.Success : ModelLogLevel.Error);

                // Kick off next queued task for this agent
                var next = _tasks.FirstOrDefault(t => t.Status == AgentTaskStatus.Queued && t.AgentId == task.AgentId);
                if (next != null)
                {
                    next.Status = AgentTaskStatus.Running;
                    next.StartedAt = DateTime.UtcNow;
                    if (agent != null) agent.CurrentTask = next.Name;
                    AddLog(task.AgentId, task.AgentName, $"Starting '{next.Name}'", ModelLogLevel.Info);
                }
                else if (agent != null)
                {
                    agent.CurrentTask = "Idle";
                    if (agent.Status == AgentStatus.Active) agent.Status = AgentStatus.Idle;
                }
            }
        }

        // Occasionally generate new tasks for idle agents
        if (_rng.NextDouble() < 0.08)
        {
            var idleAgent = _agents
                .Where(a => a.Status is AgentStatus.Active or AgentStatus.Idle
                         && !_tasks.Any(t => t.AgentId == a.Id && t.Status == AgentTaskStatus.Running))
                .OrderBy(_ => _rng.Next())
                .FirstOrDefault();

            if (idleAgent != null)
            {
                var newTask = GenerateRandomTask(idleAgent);
                newTask.Status = AgentTaskStatus.Running;
                newTask.StartedAt = DateTime.UtcNow;
                idleAgent.CurrentTask = newTask.Name;
                idleAgent.Status = AgentStatus.Active;
                _tasks.Insert(0, newTask);
                AddLog(idleAgent.Id, idleAgent.Name, $"New task assigned: '{newTask.Name}'", ModelLogLevel.Info);
            }
        }
    }

    private void MaybeAddLog()
    {
        if (_rng.NextDouble() > 0.35) return;

        var agent = _agents
            .Where(a => a.Status is AgentStatus.Active or AgentStatus.Warning)
            .OrderBy(_ => _rng.Next())
            .FirstOrDefault();

        if (agent == null) return;

        var (msg, lvl) = _rng.Next(8) switch
        {
            0 => ("Cache refreshed successfully", ModelLogLevel.Info),
            1 => ($"Processed {_rng.Next(100, 5000):N0} records in batch", ModelLogLevel.Info),
            2 => ("API rate limit approaching threshold", ModelLogLevel.Warning),
            3 => ("Connection pool reused", ModelLogLevel.Debug),
            4 => ($"Synced {_rng.Next(50, 1500):N0} records to destination", ModelLogLevel.Success),
            5 => ("Retry attempt 1/3 for failed operation", ModelLogLevel.Warning),
            6 => ("Health check passed", ModelLogLevel.Info),
            _ => ($"Heartbeat OK — uptime {agent.UptimeFormatted}", ModelLogLevel.Trace),
        };

        AddLog(agent.Id, agent.Name, msg, lvl);
    }

    private void AddLog(string? agentId, string agentName, string message, ModelLogLevel level)
    {
        _logs.Insert(0, new LogEntry
        {
            AgentId   = agentId,
            AgentName = agentName,
            Message   = message,
            Level     = level,
            Timestamp = DateTime.UtcNow,
        });
        if (_logs.Count > 500) _logs.RemoveRange(450, _logs.Count - 450);
    }

    // ─── Seed Data ───────────────────────────────────────────────────────────

    private List<Agent> SeedAgents() => new()
    {
        MakeAgent("ag-001", "DataSync Alpha",     AgentType.DataSync,      AgentStatus.Active,  "Syncing project master data",   "2.3.1", "worker-01", 4,  142, 3,  45.2, 210),
        MakeAgent("ag-002", "Report Generator",   AgentType.Reporting,     AgentStatus.Active,  "Generating weekly summary",     "1.9.0", "worker-02", 2,  87,  1,  78.5, 180),
        MakeAgent("ag-003", "ERP Integrator",     AgentType.Integration,   AgentStatus.Warning, "Awaiting API response",         "3.1.2", "worker-03", 24, 341, 12, 12.1, 160),
        MakeAgent("ag-004", "Notify Hub",         AgentType.Notification,  AgentStatus.Idle,    "Idle",                          "2.0.4", "worker-04", 6,  892, 5,  3.2,  95),
        MakeAgent("ag-005", "Data Processor",     AgentType.Processing,    AgentStatus.Active,  "Validating invoice records",    "1.5.0", "worker-05", 1,  56,  0,  91.3, 340),
        MakeAgent("ag-006", "Sync Omega",         AgentType.DataSync,      AgentStatus.Error,   "Connection failed",             "2.3.1", "worker-06", 48, 215, 28, 0.0,  120),
        MakeAgent("ag-007", "Analytics Engine",   AgentType.Processing,    AgentStatus.Active,  "Computing KPI metrics",         "1.8.2", "worker-07", 3,  103, 2,  67.8, 256),
        MakeAgent("ag-008", "Mail Dispatcher",    AgentType.Notification,  AgentStatus.Offline, "Offline",                       "2.1.0", "worker-08", 72, 567, 8,  0.0,  64),
        MakeAgent("ag-009", "Field Sync Bot",     AgentType.DataSync,      AgentStatus.Active,  "Pulling field operations data", "1.2.0", "worker-09", 5,  198, 6,  55.4, 200),
        MakeAgent("ag-010", "Compliance Checker", AgentType.Processing,    AgentStatus.Idle,    "Idle",                          "1.0.3", "worker-10", 8,  44,  1,  5.1,  128),
    };

    private static Agent MakeAgent(string id, string name, AgentType type, AgentStatus status,
        string task, string ver, string host, int hoursUp, int done, int failed, double cpu, double mem)
    {
        var agent = new Agent
        {
            Id = id, Name = name, Type = type, Status = status, CurrentTask = task,
            Version = ver, HostMachine = $"{host}.xpedeon.local",
            StartedAt = DateTime.UtcNow.AddHours(-hoursUp), LastSeen = DateTime.UtcNow,
            TasksCompleted = done, TasksFailed = failed,
            CpuUsage = cpu, MemoryUsageMB = mem, MemoryLimitMB = 512,
        };
        // Seed history
        var r = new Random(id.GetHashCode());
        for (int i = 0; i < 20; i++)
            agent.CpuHistory.Add(Math.Round(Math.Clamp(cpu + (r.NextDouble() - 0.5) * 20, 0, 100), 1));
        return agent;
    }

    private List<AgentTask> SeedTasks()
    {
        var now = DateTime.UtcNow;
        return new List<AgentTask>
        {
            MakeTask("t-001","ag-001","DataSync Alpha",     "Sync Project Data",          "ETL",        AgentTaskStatus.Running,   TaskPriority.High,     now.AddMinutes(-28), now.AddMinutes(-26), null,            65,  3420,  "Syncing active project records from ERP"),
            MakeTask("t-002","ag-002","Report Generator",  "Weekly Executive Summary",   "Report",     AgentTaskStatus.Running,   TaskPriority.Medium,   now.AddMinutes(-15), now.AddMinutes(-12), null,            40,  0,     "Compiling KPIs and metrics for weekly report"),
            MakeTask("t-003","ag-005","Data Processor",    "Invoice Validation",         "Validation", AgentTaskStatus.Running,   TaskPriority.Critical, now.AddMinutes(-5),  now.AddMinutes(-4),  null,            22,  892,   "Validating and enriching pending invoice records"),
            MakeTask("t-004","ag-007","Analytics Engine",  "KPI Computation",            "Analytics",  AgentTaskStatus.Running,   TaskPriority.Medium,   now.AddMinutes(-45), now.AddMinutes(-43), null,            88,  0,     "Calculating monthly KPI metrics"),
            MakeTask("t-005","ag-009","Field Sync Bot",    "Field Operations Pull",      "ETL",        AgentTaskStatus.Running,   TaskPriority.High,     now.AddMinutes(-10), now.AddMinutes(-9),  null,            31,  1240,  "Pulling latest field operations data"),
            MakeTask("t-006","ag-001","DataSync Alpha",    "Sync Purchase Orders",       "ETL",        AgentTaskStatus.Queued,    TaskPriority.Medium,   now.AddMinutes(-8),  null,                null,            0,   0,     "Sync PO records post project data"),
            MakeTask("t-007","ag-004","Notify Hub",        "Approval Reminders",         "Notify",     AgentTaskStatus.Queued,    TaskPriority.Low,      now.AddMinutes(-3),  null,                null,            0,   0,     "Send pending approval reminders"),
            MakeTask("t-008","ag-010","Compliance Checker","Regulatory Audit",           "Audit",      AgentTaskStatus.Queued,    TaskPriority.High,     now.AddMinutes(-1),  null,                null,            0,   0,     "Run compliance checks on recent transactions"),
            MakeTask("t-009","ag-001","DataSync Alpha",    "Sync Vendor Master",         "ETL",        AgentTaskStatus.Completed, TaskPriority.Low,      now.AddHours(-2),    now.AddHours(-2).AddMinutes(2),  now.AddHours(-1),  100, 1240,  "Vendor master data sync"),
            MakeTask("t-010","ag-002","Report Generator",  "Monthly P&L Report",         "Report",     AgentTaskStatus.Completed, TaskPriority.High,     now.AddHours(-3),    now.AddHours(-3).AddMinutes(1),  now.AddHours(-2),  100, 0,     "Monthly profit and loss statement"),
            MakeTask("t-011","ag-004","Notify Hub",        "System Maintenance Alert",   "Notify",     AgentTaskStatus.Completed, TaskPriority.Critical, now.AddHours(-4),    now.AddHours(-4),                now.AddHours(-4).AddMinutes(2), 100, 0, "Send maintenance notification to 142 users"),
            MakeTask("t-012","ag-006","Sync Omega",        "Field Data Sync",            "ETL",        AgentTaskStatus.Failed,    TaskPriority.High,     now.AddHours(-1),    now.AddMinutes(-55), now.AddMinutes(-50), 34, 412,  "Sync field operations", "Connection timeout after 3 retries"),
            MakeTask("t-013","ag-003","ERP Integrator",    "AP Invoice Push",            "Integration",AgentTaskStatus.Failed,    TaskPriority.High,     now.AddHours(-2),    now.AddHours(-2).AddMinutes(5),  now.AddHours(-1).AddMinutes(-30), 67, 800, "Push AP invoices to ERP", "External API returned 503"),
            MakeTask("t-014","ag-007","Analytics Engine",  "Cost Variance Analysis",     "Analytics",  AgentTaskStatus.Completed, TaskPriority.Medium,   now.AddHours(-5),    now.AddHours(-5).AddMinutes(2),  now.AddHours(-4), 100, 0, "Analyse cost variance vs budget"),
            MakeTask("t-015","ag-009","Field Sync Bot",    "Attendance Sync",            "ETL",        AgentTaskStatus.Completed, TaskPriority.Low,      now.AddHours(-3),    now.AddHours(-3).AddMinutes(1),  now.AddHours(-2).AddMinutes(-30), 100, 560, "Sync attendance records from field"),
        };
    }

    private static AgentTask MakeTask(string id, string agentId, string agentName, string name,
        string type, AgentTaskStatus status, TaskPriority priority,
        DateTime created, DateTime? started, DateTime? completed,
        int progress, long records, string desc, string? error = null)
    {
        var total = records > 0 ? records : type == "Report" ? 0 : new Random(id.GetHashCode()).Next(500, 5000);
        return new AgentTask
        {
            Id = id, AgentId = agentId, AgentName = agentName, Name = name,
            TaskType = type, Status = status, Priority = priority, Description = desc,
            CreatedAt = created, StartedAt = started, CompletedAt = completed,
            Progress = progress, RecordsTotal = total,
            RecordsProcessed = (long)(total * progress / 100.0),
            ErrorMessage = error,
        };
    }

    private List<LogEntry> SeedLogs()
    {
        var now = DateTime.UtcNow;
        var entries = new List<LogEntry>
        {
            MakeLog("ag-005","Data Processor",   "Invoice validation started — 892 records queued",                          ModelLogLevel.Info,    now.AddMinutes(-4)),
            MakeLog("ag-001","DataSync Alpha",   "Started sync job for 3,420 project records",                              ModelLogLevel.Info,    now.AddMinutes(-26)),
            MakeLog("ag-007","Analytics Engine", "KPI computation 88% complete",                                            ModelLogLevel.Info,    now.AddMinutes(-3)),
            MakeLog("ag-006","Sync Omega",       "Max retries reached — task failed: Connection timeout",                   ModelLogLevel.Error,   now.AddMinutes(-50)),
            MakeLog("ag-003","ERP Integrator",   "API response delayed — threshold exceeded (12s)",                         ModelLogLevel.Warning, now.AddMinutes(-2)),
            MakeLog("ag-002","Report Generator", "Monthly P&L report generated successfully (2.1 MB)",                     ModelLogLevel.Success, now.AddHours(-2)),
            MakeLog("ag-004","Notify Hub",       "Maintenance alert dispatched to 142 users",                               ModelLogLevel.Success, now.AddHours(-4)),
            MakeLog("ag-001","DataSync Alpha",   "Vendor master sync completed — 1,240 records updated",                   ModelLogLevel.Success, now.AddHours(-1)),
            MakeLog("ag-005","Data Processor",   "Found 12 validation errors in batch — flagged for review",               ModelLogLevel.Warning, now.AddMinutes(-3)),
            MakeLog("ag-009","Field Sync Bot",   "Attendance sync completed — 560 records",                                ModelLogLevel.Success, now.AddHours(-2).AddMinutes(-30)),
            MakeLog("ag-003","ERP Integrator",   "External API returned 503 — retrying in 30s",                            ModelLogLevel.Error,   now.AddHours(-1).AddMinutes(-30)),
            MakeLog("ag-007","Analytics Engine", "Cost variance analysis complete — δ+3.2% vs budget",                    ModelLogLevel.Success, now.AddHours(-4)),
            MakeLog("ag-010","Compliance Checker","Scheduled compliance audit queued",                                      ModelLogLevel.Info,    now.AddMinutes(-1)),
            MakeLog(null,    "System",           "Agent worker-06 heartbeat missed — marking as Error",                    ModelLogLevel.Error,   now.AddMinutes(-8)),
            MakeLog(null,    "System",           "Daily task schedule loaded — 24 jobs queued",                            ModelLogLevel.Info,    now.AddHours(-6)),
        };
        entries.Sort((a, b) => b.Timestamp.CompareTo(a.Timestamp));
        return entries;
    }

    private static LogEntry MakeLog(string? agentId, string agentName, string msg, ModelLogLevel lvl, DateTime ts) =>
        new() { AgentId = agentId, AgentName = agentName, Message = msg, Level = lvl, Timestamp = ts };

    private AgentTask GenerateRandomTask(Agent agent)
    {
        var tasksByType = new Dictionary<AgentType, (string[] names, string type)>
        {
            [AgentType.DataSync]      = (new[]{"Sync Project Data","Sync PO Records","Sync Vendor Master","Sync Sub-Contractor Data"}, "ETL"),
            [AgentType.Reporting]     = (new[]{"Generate Daily Report","Cost Analysis Report","Budget Variance Report"}, "Report"),
            [AgentType.Integration]   = (new[]{"Push Invoices to ERP","Sync GL Entries","Process Approval Queue"}, "Integration"),
            [AgentType.Notification]  = (new[]{"Send Approval Reminders","Dispatch Alerts","Notify Stakeholders"}, "Notify"),
            [AgentType.Processing]    = (new[]{"Validate Invoices","Process Timesheets","Compute KPIs"}, "Analytics"),
        };

        var (names, type) = tasksByType[agent.Type];
        var name = names[_rng.Next(names.Length)];
        var records = (long)_rng.Next(200, 5000);

        return new AgentTask
        {
            Id = $"t-{Guid.NewGuid():N[..8]}",
            AgentId = agent.Id,
            AgentName = agent.Name,
            Name = name,
            TaskType = type,
            Description = $"Auto-generated {type} task",
            Status = AgentTaskStatus.Queued,
            Priority = (TaskPriority)_rng.Next(4),
            CreatedAt = DateTime.UtcNow,
            Progress = 0,
            RecordsTotal = records,
        };
    }

    private string RandomErrorMessage() => _rng.Next(5) switch
    {
        0 => "Connection timeout after 3 retries",
        1 => "External API returned 503 Service Unavailable",
        2 => "Data validation failed: 24 records rejected",
        3 => "Insufficient permissions on destination",
        _ => "Unknown error — check agent logs for details",
    };

    private static double Clamp(double v, double min, double max) => Math.Max(min, Math.Min(max, v));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Dispose();
    }
}
