# Operational Safety Reliability Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove the absolute stream timeout, fix container migration exit handling, and align the health-check config listener with `usenet.providers`.

**Architecture:** Keep the slice narrow. The middleware will stop wrapping streaming request tokens, the shell entrypoint will capture migration exit codes explicitly, and the health-check service will observe the same provider config key used elsewhere in the backend.

**Tech Stack:** .NET 10, xUnit, shell entrypoint script

---

### Task 1: Lock middleware timeout semantics in tests

**Files:**
- Create: `backend.Tests/Middlewares/RequestTimeoutMiddlewareTests.cs`

- [ ] **Step 1: Write the failing tests**

Add tests that capture the request token seen by the downstream delegate:

```csharp
[Fact]
public async Task InvokeAsync_StreamingRequest_PreservesOriginalAbortToken()
{
    var seenToken = CancellationToken.None;
    var middleware = new RequestTimeoutMiddleware(ctx =>
    {
        seenToken = ctx.RequestAborted;
        return Task.CompletedTask;
    });

    var context = new DefaultHttpContext();
    context.Request.Method = HttpMethods.Get;
    context.Request.Path = "/api/stream/123";
    var original = new CancellationTokenSource();
    context.RequestAborted = original.Token;

    await middleware.InvokeAsync(context);

    Assert.Equal(original.Token, seenToken);
}

[Fact]
public async Task InvokeAsync_MetadataRequest_ReplacesAbortTokenWithTimeoutToken()
{
    var seenToken = CancellationToken.None;
    var middleware = new RequestTimeoutMiddleware(ctx =>
    {
        seenToken = ctx.RequestAborted;
        return Task.CompletedTask;
    });

    var context = new DefaultHttpContext();
    context.Request.Method = HttpMethods.Get;
    context.Request.Path = "/api/meta/123";
    var original = new CancellationTokenSource();
    context.RequestAborted = original.Token;

    await middleware.InvokeAsync(context);

    Assert.NotEqual(original.Token, seenToken);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test backend.Tests/backend.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~RequestTimeoutMiddlewareTests"`

Expected: FAIL because streaming requests currently receive a replaced timeout token.

- [ ] **Step 3: Commit**

```bash
git add backend.Tests/Middlewares/RequestTimeoutMiddlewareTests.cs
git commit -m "test: lock request-timeout middleware streaming semantics"
```

---

### Task 2: Lock the HealthCheckService config-listener behavior

**Files:**
- Modify: `backend.Tests/Services/HealthCheckServiceLeaseTests.cs`

- [ ] **Step 1: Write the failing test**

Add a test that seeds `MissingSegmentIds`, instantiates `HealthCheckService`, updates `usenet.providers`, and waits for the async cleanup task:

```csharp
[Fact]
public async Task ConfigChange_UsenetProviders_ClearsMissingSegmentIds()
{
    await using var harness = await SqliteHealthCheckHarness.CreateAsync();
    await harness.SeedMissingSegmentIdAsync("segment-a@example.test");

    var configManager = CreateRepairEnabledConfig(totalPooledConnections: 12, maxDownloadConnections: 2);
    await using var cacheScope = new TempCacheScope();
    using var liveCache = new LiveSegmentCache(cacheScope.Path);
    using var usenetClient = new RecordingUsenetStreamingClient(configManager, new WebsocketManager(), liveCache);
    _ = new HealthCheckService(configManager, usenetClient, new WebsocketManager(), new NntpLeaseState());

    configManager.UpdateValues(
    [
        new ConfigItem
        {
            ConfigName = "usenet.providers",
            ConfigValue = JsonSerializer.Serialize(new UsenetProviderConfig())
        }
    ]);

    await harness.AssertMissingSegmentIdsClearedAsync();
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test backend.Tests/backend.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~ConfigChange_UsenetProviders_ClearsMissingSegmentIds"`

Expected: FAIL because the current listener watches `usenet.host`, not `usenet.providers`.

- [ ] **Step 3: Commit**

```bash
git add backend.Tests/Services/HealthCheckServiceLeaseTests.cs
git commit -m "test: cover health-check provider config listener"
```

---

### Task 3: Add a script regression test for migration exit handling

**Files:**
- Create: `backend.Tests/Deployment/EntrypointScriptTests.cs`

- [ ] **Step 1: Write the failing test**

Add a text-level regression test for the repository root `entrypoint.sh`:

```csharp
[Fact]
public void Entrypoint_CapturesMigrationExitCodeBeforeLoggingAndExit()
{
    var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    var script = File.ReadAllText(Path.Combine(repoRoot, "entrypoint.sh"));

    Assert.Contains("MIGRATION_EXIT_CODE=$?", script);
    Assert.Contains("echo \"Database migration failed. Exiting with error code $MIGRATION_EXIT_CODE.\"", script);
    Assert.Contains("exit $MIGRATION_EXIT_CODE", script);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test backend.Tests/backend.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~EntrypointScriptTests"`

Expected: FAIL because the script currently uses bare `$?` after the conditional branch starts.

- [ ] **Step 3: Commit**

```bash
git add backend.Tests/Deployment/EntrypointScriptTests.cs
git commit -m "test: lock entrypoint migration exit-code handling"
```

---

### Task 4: Implement the production fixes

**Files:**
- Modify: `backend/Middlewares/RequestTimeoutMiddleware.cs`
- Modify: `backend/Services/HealthCheckService.cs`
- Modify: `entrypoint.sh`

- [ ] **Step 1: Remove the absolute stream timeout**

Change the middleware so streaming requests bypass the timeout wrapper:

```csharp
public async Task InvokeAsync(HttpContext context)
{
    if (IsStreamingRequest(context))
    {
        await next(context).ConfigureAwait(false);
        return;
    }

    var originalAbortToken = context.RequestAborted;
    using var cts = CancellationTokenSource.CreateLinkedTokenSource(originalAbortToken);
    cts.CancelAfter(MetadataTimeout);
    context.RequestAborted = cts.Token;

    try
    {
        await next(context).ConfigureAwait(false);
    }
    catch (OperationCanceledException) when (cts.IsCancellationRequested
                                               && !originalAbortToken.IsCancellationRequested)
    {
        if (!context.Response.HasStarted)
        {
            context.Response.StatusCode = StatusCodes.Status504GatewayTimeout;
            await context.Response.WriteAsync("Request timed out.").ConfigureAwait(false);
        }
    }
}
```

- [ ] **Step 2: Align the health-check listener with provider config**

Change:

```csharp
if (!configEventArgs.ChangedConfig.ContainsKey("usenet.host")) return;
```

to:

```csharp
if (!configEventArgs.ChangedConfig.ContainsKey("usenet.providers")) return;
```

- [ ] **Step 3: Preserve the migration exit code in entrypoint.sh**

Change:

```sh
su-exec "$USER_NAME" ./NzbWebDAV --db-migration
if [ $? -ne 0 ]; then
    echo "Database migration failed. Exiting with error code $?."
    exit $?
fi
```

to:

```sh
su-exec "$USER_NAME" ./NzbWebDAV --db-migration
MIGRATION_EXIT_CODE=$?
if [ "$MIGRATION_EXIT_CODE" -ne 0 ]; then
    echo "Database migration failed. Exiting with error code $MIGRATION_EXIT_CODE."
    exit $MIGRATION_EXIT_CODE
fi
```

- [ ] **Step 4: Run the focused tests**

Run: `dotnet test backend.Tests/backend.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~RequestTimeoutMiddlewareTests|FullyQualifiedName~ConfigChange_UsenetProviders_ClearsMissingSegmentIds|FullyQualifiedName~EntrypointScriptTests"`

Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add backend/Middlewares/RequestTimeoutMiddleware.cs backend/Services/HealthCheckService.cs entrypoint.sh backend.Tests
git commit -m "fix: harden streaming timeout and startup safety"
```

---

### Task 5: Verify the wider reliability slice

**Files:**
- Reuse existing test files

- [ ] **Step 1: Run existing health-check lease tests**

Run: `dotnet test backend.Tests/backend.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~HealthCheckServiceLeaseTests"`

Expected: PASS

- [ ] **Step 2: Run the full backend test suite**

Run: `dotnet test backend.Tests/backend.Tests.csproj -c Release --no-restore`

Expected: PASS

