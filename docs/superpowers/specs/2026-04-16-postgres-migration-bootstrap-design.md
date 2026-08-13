# Postgres Migration Bootstrap Design

**Goal**

Restore reliable startup for fresh and existing PostgreSQL-backed NZBDAV deployments without regressing SQLite behavior or changing the runtime data model.

**Problem**

The current runtime path uses `DatabaseInitialization.InitializeAsync()`, which calls EF Core migrations on PostgreSQL. Much of the historical migration chain was generated against SQLite and encodes SQLite-specific store types such as `TEXT` for `Guid` columns. On a fresh PostgreSQL database, replaying that chain through Npgsql fails during migration SQL generation and prevents startup, migrations, and multi-node deployment initialization.

**Constraints**

- Preserve existing SQLite startup and migration behavior.
- Preserve the current Postgres runtime schema and current DbContext model.
- Avoid rewriting the entire historical migration chain unless required.
- Keep the fix localized to database initialization and related tests.

**Approaches Considered**

1. Rewrite the legacy migration files to be provider-aware.

This would make `MigrateAsync()` work directly on PostgreSQL, but it is high risk because it requires touching a large historical chain and validating each old migration across both providers.

2. Add a separate PostgreSQL migration chain.

This would isolate provider concerns, but it introduces permanent dual migration maintenance and makes deployment behavior harder to reason about.

3. Bootstrap fresh PostgreSQL databases from the current model, then stamp migration history.

This avoids replaying the incompatible historical chain on fresh Postgres while preserving the current schema shape. Existing Postgres databases without migration history can still be stamped, and existing databases with history can continue normally. This is the recommended approach.

**Selected Design**

Use a provider-specific initialization path in `DatabaseInitialization`:

- For SQLite:
  - Keep the current `MigrateAsync()` path unchanged.

- For PostgreSQL with an existing application schema:
  - If `__EFMigrationsHistory` already exists and contains entries, continue with the normal migration path.
  - If the app tables exist but migration history is missing, create/stamp `__EFMigrationsHistory` with all known migrations, then seed bootstrap rows.

- For PostgreSQL with a fresh database:
  - Create the current schema from the active EF Core model using PostgreSQL metadata instead of replaying the legacy migration chain.
  - Create/stamp `__EFMigrationsHistory` with all known migrations as applied.
  - Seed bootstrap rows such as DAV roots and required config keys.
  - Skip replaying the old migrations for that fresh database initialization, because the schema is already at the current model shape.

**Implementation Boundaries**

- Primary code changes:
  - `backend/Services/DatabaseInitialization.cs`
- Supporting verification:
  - `backend.Tests/Database/DavDatabaseContextPostgresTests.cs`
  - Postgres-backed fixtures/tests that call `DatabaseInitialization.InitializeAsync()`

**Error Handling**

- If PostgreSQL schema creation fails, initialization must fail fast and return a non-zero exit path to the host process.
- Fresh database detection must be explicit and conservative:
  - no application tables
  - no migration history entries

**Testing**

- Add/update a focused PostgreSQL initialization test proving a fresh Postgres database becomes usable through `DatabaseInitialization.InitializeAsync()`.
- Verify required coordination tables exist after initialization.
- Verify bootstrap config rows (`api.key`, `api.strm-key`) exist after initialization.
- Re-run the focused backend unit/integration slice for Postgres initialization.

**Out of Scope**

- General request timeout fixes
- Jellyfin sync cleanup
- Range handling behavior changes
- Entry-point shell bug fixes

