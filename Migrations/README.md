# Entity Framework Core Migrations

This directory contains all database migrations for Xpedeon Agent Mission Control.

## Migration Strategy

- **Versioning**: Migrations are timestamped (YYYYMMDDhhmmss format)
- **Database Providers**: Migrations support both SQL Server and SQLite
- **Tracking**: Schema version tracked in `__EFMigrationsHistory` table
- **Application**: Migrations are applied automatically on app startup via `db.Database.MigrateAsync()`

## Migration Files

### InitialSchema (20260430000000)
- Initial database schema
- All core entities: Agents, Tasks, LLMProviders, Swarms, Skills, Workflows, etc.
- Foreign keys, indexes, and constraints

## Adding New Migrations

When adding new features that require database changes:

1. **Update Models** — Add/modify entity classes in `/Models/`
2. **Generate Migration** — (Requires .NET SDK installed)
   ```bash
   dotnet ef migrations add FeatureName --context AppDbContext
   ```
3. **Review Migration** — Check the generated migration file
4. **Test** — Run on both SQL Server and SQLite test databases
5. **Commit** — Include both migration file and snapshot in git

## Applying Migrations

### Automatic (on app startup)
Migrations are applied automatically when the app starts via `Program.cs`:
```csharp
await db.Database.MigrateAsync();
```

### Manual (if needed)
```bash
dotnet ef database update --context AppDbContext
```

## Rolling Back Migrations

If a migration causes issues, revert to the previous migration:

```bash
dotnet ef database update PreviousMigrationName --context AppDbContext
```

## Disaster Recovery

### Backup Before Migration
Always backup the database before applying migrations in production:
```bash
# SQL Server
BACKUP DATABASE [YourDbName] TO DISK = 'C:\backup\xpedeon_backup.bak'

# SQLite
cp xpedeon.db xpedeon.db.backup
```

### Restore If Migration Fails
```bash
# SQL Server
RESTORE DATABASE [YourDbName] FROM DISK = 'C:\backup\xpedeon_backup.bak'

# SQLite
cp xpedeon.db.backup xpedeon.db
```

## Key Constraints

- **Cascading Deletes**: Agent deletion cascades to Tasks, Memories, Skills
- **Soft Deletes**: Not used; actual deletion preferred with event logging
- **Foreign Keys**: All relationships enforced with proper constraints
- **Nullable**: Carefully managed; mostly non-null for data integrity

## Testing Migrations

Run integration tests to validate migrations:
```bash
dotnet test XpedeonAgentMissionControl.Tests
```

Tests create isolated in-memory SQLite databases and validate schema.

## References

- [EF Core Migrations](https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/)
- CLAUDE.md — Database configuration
- PRODUCTION_ROADMAP.md — Phase 1 plan
