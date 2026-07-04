# Production Readiness Roadmap - Xpedeon Agent Mission Control

**Status:** In Progress  
**Target:** Complete all 4 phases to reach production-grade system  
**Methodology:** Land and Expand (complete each phase before polishing)  
**Total Effort:** 490-590 hours | 13 weeks | 4 phases  
**Start Date:** 2026-04-30

---

## Phase Overview

### Phase 1: Foundation Hardening (Weeks 1-3) | 120-150 hrs
**Status:** ⏳ In Progress  
**Theme:** Database Resilience + Critical Security Baseline

- [ ] 1.1 Database Migrations (EF Core)
- [ ] 1.2 API Authentication (JWT + API Key)
- [ ] 1.3 Configuration Encryption (DPAPI/RSA)
- [ ] 1.4 Input Validation Middleware
- [ ] 1.5 Health Check Infrastructure
- [ ] 1.6 Audit Logging Framework

**Go-Live Criteria:**
- All table schemas in migrations (no EnsureCreated())
- Auth working end-to-end
- Encryption functional for secrets
- Health check endpoint returning status
- Audit logs recording operations

---

### Phase 2: Cost Controls & Rate Limiting (Weeks 4-6) | 80-100 hrs
**Status:** ⏳ Pending (Phase 1 prerequisite)  
**Theme:** Spend Management + DoS Protection

- [ ] 2.1 Spend Limits Model
- [ ] 2.2 Rate Limiting Middleware
- [ ] 2.3 Token Budget Enforcement
- [ ] 2.4 Cost Alerts & Escalation

**Go-Live Criteria:**
- Per-agent spend limits enforced
- Rate limiting blocks overages (429 responses)
- Token budget tracked
- Alerts triggered at thresholds

---

### Phase 3: Reliability & Job Queue (Weeks 7-10) | 150-180 hrs
**Status:** ⏳ Pending (Phase 1 prerequisite)  
**Theme:** Persistent Background Processing + Failure Resilience

- [ ] 3.1 Job Queue Implementation (Quartz.NET)
- [ ] 3.2 Retry Policies & Exponential Backoff
- [ ] 3.3 Circuit Breaker Pattern
- [ ] 3.4 Task Persistence & Resumption
- [ ] 3.5 Disaster Recovery Procedures

**Go-Live Criteria:**
- Job queue persists (survives app crashes)
- Retry policy executes automatically
- Circuit breaker blocks failed providers
- Task checkpoints saved and resumable
- Backup/restore cycle tested

---

### Phase 4: Observability & Hardening (Weeks 11-14) | 140-160 hrs
**Status:** ⏳ Pending (Phase 1-3 prerequisite)  
**Theme:** Monitoring, Logging, SLAs, Production Best Practices

- [ ] 4.1 Structured Logging (Serilog)
- [ ] 4.2 Prometheus Metrics & Grafana
- [ ] 4.3 SLA Definition & Enforcement
- [ ] 4.4 Runbooks & Operational Docs
- [ ] 4.5 Production Deployment & Infrastructure
- [ ] 4.6 Performance Optimization & Load Testing

**Go-Live Criteria:**
- Logs searchable with correlation IDs
- Metrics exposed at /metrics
- Grafana dashboard operational
- SLA violations trigger alerts
- Infrastructure as code deployed
- Load test: 100 concurrent users stable

---

## Critical Path Dependencies

```
Phase 1 (Foundation) ──→ Phase 2 (Cost Controls)
         ├──────────→ Phase 3 (Reliability)
                      └──→ Phase 4 (Observability)
```

**Can parallelize after Phase 1:**
- Phase 2 can start Week 2 (auth foundation ready)
- Phase 3 can start Week 3 (migrations complete)
- Phase 4 prep can start concurrently with Phase 3

---

## Key Metrics to Track

- **Database:** Migration count, schema version, backup success rate
- **Security:** API keys rotated, encryption key health, failed auth attempts
- **Performance:** Task execution time, LLM latency (p50, p95, p99)
- **Reliability:** Job queue depth, circuit breaker trips, retry success rate
- **Cost:** Daily spend, token usage, forecast accuracy
- **Observability:** Log ingest rate, metric scrape success, alert accuracy

---

## Critical Files (Focus Areas)

1. `/Program.cs` — DI registration, middleware pipeline
2. `/Data/AppDbContext.cs` — Data models, migrations
3. `/Services/TaskService.cs` — Task execution (replace Task.Run())
4. `/Services/LLMExecutionService.cs` — LLM routing (add retry/circuit breaker)
5. `/Configuration/DatabaseConfig.cs` — Config management

---

## Team Assignment (Recommended)

- **Backend Lead:** Phase 1 core, coordinator for all phases
- **DevOps/Infrastructure:** Phases 3.5, 4.5
- **Senior Developer:** Complex items (job queue, circuit breaker, resilience)
- **QA/Testing:** All phases, load testing, disaster recovery validation

---

## Progress Tracking

| Phase | Item | Status | Owner | ETA |
|-------|------|--------|-------|-----|
| 1 | Migrations | ⏳ | - | - |
| 1 | Authentication | ⏳ | - | - |
| 1 | Encryption | ⏳ | - | - |
| 1 | Validation | ⏳ | - | - |
| 1 | Health Checks | ⏳ | - | - |
| 1 | Audit Logging | ⏳ | - | - |
| 2 | Spend Limits | ⏳ | - | - |
| 2 | Rate Limiting | ⏳ | - | - |
| 2 | Token Budget | ⏳ | - | - |
| 2 | Cost Alerts | ⏳ | - | - |
| 3 | Job Queue | ⏳ | - | - |
| 3 | Retry Policies | ⏳ | - | - |
| 3 | Circuit Breaker | ⏳ | - | - |
| 3 | Task Persistence | ⏳ | - | - |
| 3 | Disaster Recovery | ⏳ | - | - |
| 4 | Structured Logging | ⏳ | - | - |
| 4 | Prometheus/Grafana | ⏳ | - | - |
| 4 | SLAs | ⏳ | - | - |
| 4 | Runbooks | ⏳ | - | - |
| 4 | Infrastructure | ⏳ | - | - |
| 4 | Load Testing | ⏳ | - | - |

---

## Go-Live Readiness Checklist

### Security
- [ ] All API endpoints require authentication
- [ ] Database encrypted at rest
- [ ] Encryption keys in HSM or Key Vault
- [ ] No hardcoded secrets in code
- [ ] SAST security audit passed

### Reliability
- [ ] Migrations tested on prod-like data
- [ ] Backup/restore cycle successful
- [ ] Job queue distributed across instances
- [ ] Circuit breaker tested (provider failure)
- [ ] Retry policy tested (transient errors)
- [ ] Zero-downtime deployment tested

### Cost Controls
- [ ] Per-agent spend limits enforced
- [ ] Rate limiting blocks gracefully
- [ ] Cost forecast accurate ±10%
- [ ] Alerts trigger at thresholds

### Observability
- [ ] Logs searchable &lt;1s latency
- [ ] Prometheus metrics stable
- [ ] Grafana dashboard reflects reality
- [ ] SLA definitions match business
- [ ] Ops team trained on runbooks

---

## Rollback Strategy

Each phase has exit criteria. If any go-live criteria fail:

1. **Phase 1 Rollback:** Revert to EnsureCreated(), skip auth (restore from backup)
2. **Phase 2 Rollback:** Disable cost limits via feature flag
3. **Phase 3 Rollback:** Revert to Task.Run() (accept crash risk temporarily)
4. **Phase 4 Rollback:** Disable monitoring (logs still functional)

---

## References

- CLAUDE.md — Architecture & tech stack
- AGENTS.md — Agent templates and roles
- `/docs/runbooks/` — Operational guides (Phase 4)
- `XpedeonAgentMissionControl.Tests/` — Test harness for validation

---

**Last Updated:** 2026-04-30  
**Next Review:** After Phase 1 completion
