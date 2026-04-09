ALTER TABLE Tasks ADD COLUMN WorkflowRunId TEXT NULL;
ALTER TABLE Tasks ADD COLUMN WorkflowStepId TEXT NULL;
ALTER TABLE Tasks ADD COLUMN WorkflowStepRunId TEXT NULL;

CREATE TABLE IF NOT EXISTS Workflows (
    Id TEXT NOT NULL PRIMARY KEY,
    Name TEXT NOT NULL,
    Description TEXT NOT NULL,
    IsActive INTEGER NOT NULL,
    Version TEXT NOT NULL,
    CreatedAt TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS WorkflowSteps (
    Id TEXT NOT NULL PRIMARY KEY,
    WorkflowId TEXT NOT NULL,
    StepOrder INTEGER NOT NULL,
    Name TEXT NOT NULL,
    StepType INTEGER NOT NULL,
    AgentId TEXT NULL,
    SwarmId TEXT NULL,
    StaticInput TEXT NULL,
    InputSource INTEGER NOT NULL,
    PromptOverride TEXT NULL,
    RequireApproval INTEGER NOT NULL,
    TimeoutSeconds INTEGER NOT NULL,
    ContinueOnFailure INTEGER NOT NULL,
    FOREIGN KEY (WorkflowId) REFERENCES Workflows(Id) ON DELETE CASCADE,
    FOREIGN KEY (AgentId) REFERENCES Agents(Id) ON DELETE SET NULL,
    FOREIGN KEY (SwarmId) REFERENCES Swarms(Id) ON DELETE SET NULL
);

CREATE TABLE IF NOT EXISTS WorkflowRuns (
    Id TEXT NOT NULL PRIMARY KEY,
    WorkflowId TEXT NOT NULL,
    WorkflowNameSnapshot TEXT NOT NULL,
    Status INTEGER NOT NULL,
    Input TEXT NULL,
    Output TEXT NULL,
    ErrorMessage TEXT NULL,
    CreatedAt TEXT NOT NULL,
    StartedAt TEXT NULL,
    CompletedAt TEXT NULL,
    CurrentStepOrder INTEGER NOT NULL,
    FOREIGN KEY (WorkflowId) REFERENCES Workflows(Id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS WorkflowStepRuns (
    Id TEXT NOT NULL PRIMARY KEY,
    WorkflowRunId TEXT NOT NULL,
    WorkflowStepId TEXT NOT NULL,
    StepNameSnapshot TEXT NOT NULL,
    StepOrder INTEGER NOT NULL,
    Status INTEGER NOT NULL,
    Input TEXT NULL,
    Output TEXT NULL,
    ErrorMessage TEXT NULL,
    AgentTaskId TEXT NULL,
    CreatedAt TEXT NOT NULL,
    StartedAt TEXT NULL,
    CompletedAt TEXT NULL,
    FOREIGN KEY (WorkflowRunId) REFERENCES WorkflowRuns(Id) ON DELETE CASCADE,
    FOREIGN KEY (WorkflowStepId) REFERENCES WorkflowSteps(Id) ON DELETE RESTRICT,
    FOREIGN KEY (AgentTaskId) REFERENCES Tasks(Id) ON DELETE SET NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS IX_WorkflowSteps_WorkflowId_StepOrder
    ON WorkflowSteps (WorkflowId, StepOrder);

CREATE INDEX IF NOT EXISTS IX_WorkflowRuns_WorkflowId_CreatedAt
    ON WorkflowRuns (WorkflowId, CreatedAt DESC);

CREATE INDEX IF NOT EXISTS IX_WorkflowStepRuns_WorkflowRunId_StepOrder
    ON WorkflowStepRuns (WorkflowRunId, StepOrder);

CREATE INDEX IF NOT EXISTS IX_Tasks_WorkflowRunId
    ON Tasks (WorkflowRunId);

CREATE INDEX IF NOT EXISTS IX_Tasks_WorkflowStepId
    ON Tasks (WorkflowStepId);

CREATE INDEX IF NOT EXISTS IX_Tasks_WorkflowStepRunId
    ON Tasks (WorkflowStepRunId);
