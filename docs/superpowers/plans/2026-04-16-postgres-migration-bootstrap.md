# Postgres Migration Bootstrap Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make fresh and legacy PostgreSQL NZBDAV databases initialize successfully without replaying the incompatible SQLite-shaped migration chain.

**Architecture:** Keep SQLite on the existing migration path. Add a PostgreSQL-specific initialization branch in `DatabaseInitialization` that detects fresh databases, creates the current schema directly, stamps migration history, and seeds required bootstrap data. Existing Postgres databases keep the current history/bootstrap behavior.

**Tech Stack:** .NET 10, EF Core, Npgsql, xUnit, Docker-backed Postgres integration tests

---

### Task 1: Lock the failing Postgres initialization behavior in tests

**Files:**
- Modify: `backend.Tests/Database/DavDatabaseContextPostgresTests.cs`

- [ ] **Step 1: Write the failing test**

Add or update a Postgres integration test that exercises the runtime initialization wrapper instead of bare `MigrateAsync()`:

```csharp
[SkippableFact]
public async Task DatabaseInitialization_FreshPostgres_CreatesCurrentSchemaAndBootstrapRows()
{
    Skip.IfNot(_fixture.IsAvailable, "Docker is required for this integration test.");

    await _fixture.ResetAsync();

    using var environment = new TemporaryEnvironment(("DATABASE_URL", _fixture.ConnectionString));
    await using var dbContext = new DavDatabaseContext();

    await DatabaseInitialization.InitializeAsync(dbContext, CancellationToken.None);

    Assert.True(await TableExistsAsync(dbContext, "websocket_outbox"));
    Assert.True(await TableExistsAsync(dbContext, "auth_failures"));
    Assert.True(await TableExistsAsync(dbContext, "connection_pool_claims"));
    Assert.True(await dbContext.ConfigItems.AnyAsync(x => x.ConfigName == "api.key"));
    Assert.True(await dbContext.ConfigItems.AnyAsync(x => x.ConfigName == "api.strm-key"));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test backend.Tests/backend.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~DatabaseInitialization_FreshPostgres_CreatesCurrentSchemaAndBootstrapRows"`

Expected: FAIL during PostgreSQL initialization before the bootstrap rows/tables can be asserted.

- [ ] **Step 3: Commit**

```bash
git add backend.Tests/Database/DavDatabaseContextPostgresTests.cs
git commit -m "test: cover fresh postgres initialization bootstrap path"
```

---

### Task 2: Add provider-specific fresh-Postgres bootstrap logic

**Files:**
- Modify: `backend/Services/DatabaseInitialization.cs`

- [ ] **Step 1: Write the failing helper-level test target**

Use the failing integration test from Task 1 as the red test. Do not add production code first.

- [ ] **Step 2: Implement fresh-Postgres detection and bootstrap**

Restructure initialization so PostgreSQL follows a decision tree:

```csharp
public static async Task InitializeAsync(
    DavDatabaseContext databaseContext,
    CancellationToken cancellationToken,
    string? targetMigration = null)
{
    var isPostgres = !string.IsNullOrEmpty(EnvironmentUtil.GetDatabaseUrl());
    if (!isPostgres)
    {
        await databaseContext.Database
            .MigrateAsync(targetMigration, cancellationToken)
            .ConfigureAwait(false);
        return;
    }

    var state = await InspectPostgresStateAsync(databaseContext, cancellationToken).ConfigureAwait(false);

    if (state.IsFreshDatabase)
    {
        await databaseContext.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        await StampAllMigrationsAsync(databaseContext, cancellationToken).ConfigureAwait(false);
        await SeedPostgresBootstrapDataAsync(databaseContext, cancellationToken).ConfigureAwait(false);
        return;
    }

    if (state.HasApplicationTables && !state.HasMigrationHistoryEntries)
    {
        await StampAllMigrationsAsync(databaseContext, cancellationToken).ConfigureAwait(false);
        await SeedPostgresBootstrapDataAsync(databaseContext, cancellationToken).ConfigureAwait(false);
        return;
    }

    await databaseContext.Database
        .MigrateAsync(targetMigration, cancellationToken)
        .ConfigureAwait(false);

    await SeedPostgresBootstrapDataAsync(databaseContext, cancellationToken).ConfigureAwait(false);
}
```

Add small internal helpers for:
- inspecting fresh-vs-legacy-vs-normal Postgres state
- ensuring the migration history table exists
- stamping all migrations safely

- [ ] **Step 3: Run test to verify it passes**

Run: `dotnet test backend.Tests/backend.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~DatabaseInitialization_FreshPostgres_CreatesCurrentSchemaAndBootstrapRows"`

Expected: PASS

- [ ] **Step 4: Commit**

```bash
git add backend/Services/DatabaseInitialization.cs
git commit -m "fix: bootstrap fresh postgres databases from current schema"
```

---

### Task 3: Verify no SQLite regression in initialization

**Files:**
- Reuse existing tests in `backend.Tests/Database/DavDatabaseContextMigrationTests.cs`
- Reuse existing tests in `backend.Tests/Services/StartupEncryptionCheckTests.cs`

- [ ] **Step 1: Run focused SQLite migration test**

Run: `dotnet test backend.Tests/backend.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~DavDatabaseContextMigrationTests.MigrateAsync_CreatesRoleAwareNntpLeaseTables_OnSqlite"`

Expected: PASS

- [ ] **Step 2: Run focused SQLite startup-encryption test**

Run: `dotnet test backend.Tests/backend.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~StartupEncryptionCheckTests.RunAsync_WithOnlyBootstrapKeys_DoesNotWriteMigrationMarker"`

Expected: PASS

- [ ] **Step 3: Commit if code changes were needed**

```bash
git add backend/Services/DatabaseInitialization.cs backend.Tests
git commit -m "test: verify postgres bootstrap change does not regress sqlite startup"
```

---

### Task 4: Verify broader Postgres-backed initialization slice

**Files:**
- Reuse existing Postgres-backed tests in:
  - `backend.Tests/Database/DavDatabaseContextPostgresTests.cs`
  - `backend.Tests/Api/Filters/PostgresAuthFailureTrackerTests.cs`
  - `backend.Tests/Clients/Usenet/Caching/SharedHeaderCacheTests.cs`

- [ ] **Step 1: Run Postgres database tests**

Run: `dotnet test backend.Tests/backend.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~DavDatabaseContextPostgresTests"`

Expected: PASS for the Postgres database initialization assertions.

- [ ] **Step 2: Run Postgres auth failure tracker tests**

Run: `dotnet test backend.Tests/backend.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~PostgresAuthFailureTrackerTests"`

Expected: PASS

- [ ] **Step 3: Run Postgres shared header cache tests**

Run: `dotnet test backend.Tests/backend.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~SharedHeaderCacheTests"`

Expected: PASS for Postgres-backed fixture initialization.

- [ ] **Step 4: Commit**

```bash
git add backend/Services/DatabaseInitialization.cs backend.Tests
git commit -m "test: verify postgres initialization path across integration fixtures"
```

---

### Task 5: Reviewer pass and final verification

**Files:**
- Review-only task

- [ ] **Step 1: Run the final focused verification batch**

Run:

```bash
dotnet test backend.Tests/backend.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~DavDatabaseContextPostgresTests|FullyQualifiedName~PostgresAuthFailureTrackerTests|FullyQualifiedName~SharedHeaderCacheTests|FullyQualifiedName~DavDatabaseContextMigrationTests.MigrateAsync_CreatesRoleAwareNntpLeaseTables_OnSqlite|FullyQualifiedName~StartupEncryptionCheckTests.RunAsync_WithOnlyBootstrapKeys_DoesNotWriteMigrationMarker"
```

Expected: PASS

- [ ] **Step 2: Request code review**

Use reviewer context:

```text
WHAT_WAS_IMPLEMENTED: Postgres database initialization bootstrap for fresh and legacy databases
PLAN_OR_REQUIREMENTS: docs/superpowers/specs/2026-04-16-postgres-migration-bootstrap-design.md and this plan
DESCRIPTION: Replaced fresh-Postgres migration replay with schema bootstrap + migration history stamping while preserving SQLite behavior
```

- [ ] **Step 3: Address review findings and re-run the same verification batch**

Run the same command from Step 1 after any review fixes.

