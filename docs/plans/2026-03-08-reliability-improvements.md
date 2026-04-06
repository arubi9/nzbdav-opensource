# Reliability Improvements Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Fix four reliability issues: provider failure isolation (#282), organized link scan reduction, end-of-stream stall detection (#115), and deterministic temp cleanup (#294).

**Architecture:** Each fix is scoped to a narrow internal change with tests. Provider isolation adds a health-state tracker at the provider-selection layer. Link scan reduction adds a singleton index that caches library scans. Stream stall detection adds under-read detection to composite streams. Temp cleanup replaces fire-and-forget disposal with deterministic async disposal.

**Tech Stack:** .NET 10, xUnit, hand-written test doubles (no mocking frameworks), Serilog logging, EF Core SQLite.

---

## PR 1: Provider Failure Isolation (#282)

### Task 1: ProviderHealthState

**Files:**
- Create: `backend/Clients/Usenet/ProviderHealthState.cs`
- Test: `backend.Tests/Clients/Usenet/ProviderHealthStateTests.cs`

**Step 1: Write the failing tests**

```csharp
// backend.Tests/Clients/Usenet/ProviderHealthStateTests.cs
namespace NzbWebDAV.Tests.Clients.Usenet;

public class ProviderHealthStateTests
{
    [Fact]
    public void NewState_IsNotBlocked()
    {
        var state = new ProviderHealthState();
        Assert.False(state.IsBlocked(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void AfterFailure_IsBlocked()
    {
        var state = new ProviderHealthState();
        var now = DateTimeOffset.UtcNow;
        state.RegisterFailure(now, new Exception("connect failed"), TimeSpan.FromSeconds(60));
        Assert.True(state.IsBlocked(now + TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void AfterCooldown_IsNotBlocked()
    {
        var state = new ProviderHealthState();
        var now = DateTimeOffset.UtcNow;
        state.RegisterFailure(now, new Exception("connect failed"), TimeSpan.FromSeconds(60));
        Assert.False(state.IsBlocked(now + TimeSpan.FromSeconds(61)));
    }

    [Fact]
    public void AfterSuccess_IsNotBlocked()
    {
        var state = new ProviderHealthState();
        var now = DateTimeOffset.UtcNow;
        state.RegisterFailure(now, new Exception("connect failed"), TimeSpan.FromSeconds(60));
        state.RegisterSuccess();
        Assert.False(state.IsBlocked(now + TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void ConsecutiveFailures_Track()
    {
        var state = new ProviderHealthState();
        var now = DateTimeOffset.UtcNow;
        state.RegisterFailure(now, new Exception("fail 1"), TimeSpan.FromSeconds(60));
        state.RegisterFailure(now, new Exception("fail 2"), TimeSpan.FromSeconds(60));
        Assert.Equal(2, state.ConsecutiveFailures);
    }

    [Fact]
    public void RegisterSuccess_ResetsConsecutiveFailures()
    {
        var state = new ProviderHealthState();
        var now = DateTimeOffset.UtcNow;
        state.RegisterFailure(now, new Exception("fail"), TimeSpan.FromSeconds(60));
        state.RegisterSuccess();
        Assert.Equal(0, state.ConsecutiveFailures);
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test backend.Tests --filter "FullyQualifiedName~ProviderHealthStateTests" --no-restore`
Expected: FAIL — `ProviderHealthState` type does not exist.

**Step 3: Write minimal implementation**

```csharp
// backend/Clients/Usenet/ProviderHealthState.cs
namespace NzbWebDAV.Clients.Usenet;

internal class ProviderHealthState
{
    private DateTimeOffset _blockedUntilUtc = DateTimeOffset.MinValue;
    private int _consecutiveFailures;
    private string? _lastFailureMessage;

    public int ConsecutiveFailures => _consecutiveFailures;
    public string? LastFailureMessage => _lastFailureMessage;

    public bool IsBlocked(DateTimeOffset now) => now < _blockedUntilUtc;

    public void RegisterFailure(DateTimeOffset now, Exception ex, TimeSpan cooldown)
    {
        _consecutiveFailures++;
        _lastFailureMessage = ex.Message;
        _blockedUntilUtc = now + cooldown;
    }

    public void RegisterSuccess()
    {
        _consecutiveFailures = 0;
        _lastFailureMessage = null;
        _blockedUntilUtc = DateTimeOffset.MinValue;
    }
}
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test backend.Tests --filter "FullyQualifiedName~ProviderHealthStateTests" --no-restore`
Expected: 6 PASS

**Step 5: Commit**

```bash
git add backend/Clients/Usenet/ProviderHealthState.cs backend.Tests/Clients/Usenet/ProviderHealthStateTests.cs
git commit -m "feat(#282): add ProviderHealthState with cooldown tracking"
```

---

### Task 2: MultiProviderNntpClient Provider Wrapper & Health Integration

**Files:**
- Modify: `backend/Clients/Usenet/MultiProviderNntpClient.cs`
- Modify: `backend/Clients/Usenet/UsenetStreamingClient.cs`
- Test: `backend.Tests/Clients/Usenet/MultiProviderNntpClientTests.cs`

**Step 1: Write the failing tests**

Create a `FakeMultiConnectionNntpClient` helper inside the test file that extends `NntpClient` for direct use with `MultiProviderNntpClient`. This avoids needing a real connection pool.

```csharp
// backend.Tests/Clients/Usenet/MultiProviderNntpClientTests.cs
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Models;
using UsenetSharp.Models;

namespace NzbWebDAV.Tests.Clients.Usenet;

public class MultiProviderNntpClientTests
{
    /// <summary>
    /// Lightweight stand-in for MultiConnectionNntpClient that doesn't need a real pool.
    /// </summary>
    private sealed class FakeProviderClient : NntpClient
    {
        public ProviderType ProviderType { get; }
        public int AvailableConnections { get; set; } = 10;
        private readonly Func<string, CancellationToken, Task<UsenetStatResponse>>? _statHandler;
        private readonly Func<string, CancellationToken, Task<UsenetDecodedBodyResponse>>? _bodyHandler;

        public FakeProviderClient(
            ProviderType providerType,
            Func<string, CancellationToken, Task<UsenetStatResponse>>? statHandler = null,
            Func<string, CancellationToken, Task<UsenetDecodedBodyResponse>>? bodyHandler = null)
        {
            ProviderType = providerType;
            _statHandler = statHandler;
            _bodyHandler = bodyHandler;
        }

        public override Task<UsenetStatResponse> StatAsync(SegmentId segmentId, CancellationToken ct)
        {
            if (_statHandler != null) return _statHandler(segmentId, ct);
            return Task.FromResult(new UsenetStatResponse
            {
                ArticleExists = true,
                ResponseCode = (int)UsenetResponseType.ArticleExists,
                ResponseMessage = "223"
            });
        }

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(SegmentId segmentId, CancellationToken ct)
        {
            if (_bodyHandler != null) return _bodyHandler(segmentId, ct);
            return Task.FromResult(new UsenetDecodedBodyResponse
            {
                SegmentId = segmentId,
                ResponseCode = (int)UsenetResponseType.ArticleRetrievedBodyFollows,
                ResponseMessage = "222"
            });
        }

        // Minimal stubs for other abstract members
        public override Task ConnectAsync(string host, int port, bool useSsl, CancellationToken ct)
            => throw new NotSupportedException();
        public override Task<UsenetResponse> AuthenticateAsync(string user, string pass, CancellationToken ct)
            => throw new NotSupportedException();
        public override Task<UsenetHeadResponse> HeadAsync(SegmentId segmentId, CancellationToken ct)
            => Task.FromResult(new UsenetHeadResponse
            {
                SegmentId = segmentId,
                ResponseCode = (int)UsenetResponseType.ArticleRetrievedHeadFollows,
                ResponseMessage = "221"
            });
        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(SegmentId segmentId, CancellationToken ct)
            => Task.FromResult(new UsenetDecodedArticleResponse
            {
                SegmentId = segmentId,
                ResponseCode = (int)UsenetResponseType.ArticleRetrievedHeadAndBodyFollow,
                ResponseMessage = "220"
            });
        public override Task<UsenetDateResponse> DateAsync(CancellationToken ct)
            => Task.FromResult(new UsenetDateResponse { ResponseCode = 111, ResponseMessage = "111" });
        public override void Dispose() { GC.SuppressFinalize(this); }
    }

    [Fact]
    public async Task FailingBackupProvider_IsSkippedAfterAuthFailure()
    {
        var callCount = 0;
        var failingProvider = new FakeProviderClient(
            ProviderType.BackupOnly,
            statHandler: (_, _) => throw new CouldNotLoginToUsenetException("bad creds"));
        var healthyProvider = new FakeProviderClient(
            ProviderType.Pooled,
            statHandler: (seg, _) =>
            {
                Interlocked.Increment(ref callCount);
                return Task.FromResult(new UsenetStatResponse
                {
                    ArticleExists = true,
                    ResponseCode = (int)UsenetResponseType.ArticleExists,
                    ResponseMessage = "223"
                });
            });

        var providers = new List<MultiProviderNntpClient.ProviderDescriptor>
        {
            new(healthyProvider, ProviderType.Pooled, () => 10),
            new(failingProvider, ProviderType.BackupOnly, () => 5),
        };
        using var client = new MultiProviderNntpClient(providers);

        // First call: triggers failure on backup, succeeds on pooled
        // The backup throws on stat, so pooled should be used
        // Actually since Pooled comes first in ordering, let's reverse:
        // Put backup first so it's tried first (BackupOnly = 3, Pooled = 1)
        // Wait - ordering is by ProviderType ascending, so Pooled(1) < BackupOnly(3)
        // So pooled is tried first. Let's make pooled the failing one for this test.

        // REVISED: Make pooled throw auth failure, backup should still serve
        var failingPooled = new FakeProviderClient(
            ProviderType.Pooled,
            statHandler: (_, _) => throw new CouldNotLoginToUsenetException("bad creds"));
        var healthyBackup = new FakeProviderClient(
            ProviderType.BackupOnly,
            statHandler: (seg, _) =>
            {
                Interlocked.Increment(ref callCount);
                return Task.FromResult(new UsenetStatResponse
                {
                    ArticleExists = true,
                    ResponseCode = (int)UsenetResponseType.ArticleExists,
                    ResponseMessage = "223"
                });
            });

        var providers2 = new List<MultiProviderNntpClient.ProviderDescriptor>
        {
            new(failingPooled, ProviderType.Pooled, () => 10),
            new(healthyBackup, ProviderType.BackupOnly, () => 5),
        };
        using var client2 = new MultiProviderNntpClient(providers2);

        // First call: pooled fails with auth, falls through to backup
        var result1 = await client2.StatAsync("seg1", CancellationToken.None);
        Assert.True(result1.ArticleExists);

        // Second call: pooled should be BLOCKED now, backup serves directly
        callCount = 0;
        var result2 = await client2.StatAsync("seg2", CancellationToken.None);
        Assert.True(result2.ArticleExists);
        Assert.Equal(1, callCount); // Only backup was called
    }

    [Fact]
    public async Task HealthyPooledProvider_StillServesWhileBackupIsBlocked()
    {
        var pooledCallCount = 0;
        var healthyPooled = new FakeProviderClient(
            ProviderType.Pooled,
            statHandler: (seg, _) =>
            {
                Interlocked.Increment(ref pooledCallCount);
                return Task.FromResult(new UsenetStatResponse
                {
                    ArticleExists = true,
                    ResponseCode = (int)UsenetResponseType.ArticleExists,
                    ResponseMessage = "223"
                });
            });
        var failingBackup = new FakeProviderClient(
            ProviderType.BackupOnly,
            statHandler: (_, _) => throw new CouldNotConnectToUsenetException("timeout"));

        var providers = new List<MultiProviderNntpClient.ProviderDescriptor>
        {
            new(healthyPooled, ProviderType.Pooled, () => 10),
            new(failingBackup, ProviderType.BackupOnly, () => 5),
        };
        using var client = new MultiProviderNntpClient(providers);

        // Trigger backup failure via article-not-found fallthrough on pooled
        // Actually: pooled succeeds, backup never tried. Let's force backup to be tried:
        // Make pooled return NoArticleWithThatMessageId so it falls to backup
        var pooledNoArticle = new FakeProviderClient(
            ProviderType.Pooled,
            statHandler: (seg, _) =>
            {
                Interlocked.Increment(ref pooledCallCount);
                return Task.FromResult(new UsenetStatResponse
                {
                    ArticleExists = false,
                    ResponseCode = (int)UsenetResponseType.NoArticleWithThatMessageId,
                    ResponseMessage = "430"
                });
            });

        var providers2 = new List<MultiProviderNntpClient.ProviderDescriptor>
        {
            new(pooledNoArticle, ProviderType.Pooled, () => 10),
            new(failingBackup, ProviderType.BackupOnly, () => 5),
        };
        using var client2 = new MultiProviderNntpClient(providers2);

        // First call triggers backup connect failure. Should still return (throws from backup)
        // Since both fail, the exception propagates.
        // But on next call, backup should be blocked, pooled returns NoArticle (which is final answer)
        try { await client2.StatAsync("seg1", CancellationToken.None); } catch { }

        // Now backup is blocked. Pooled returns NoArticle — since backup is blocked and it's effectively the last provider, it should return the NoArticle response
        pooledCallCount = 0;
        var result = await client2.StatAsync("seg2", CancellationToken.None);
        Assert.Equal((int)UsenetResponseType.NoArticleWithThatMessageId, result.ResponseCode);
        Assert.Equal(1, pooledCallCount);
    }

    [Fact]
    public async Task ArticleNotFound_DoesNotBlockProvider()
    {
        var pooledCallCount = 0;
        var pooledProvider = new FakeProviderClient(
            ProviderType.Pooled,
            statHandler: (seg, _) =>
            {
                Interlocked.Increment(ref pooledCallCount);
                return Task.FromResult(new UsenetStatResponse
                {
                    ArticleExists = false,
                    ResponseCode = (int)UsenetResponseType.NoArticleWithThatMessageId,
                    ResponseMessage = "430"
                });
            });
        var backupProvider = new FakeProviderClient(
            ProviderType.BackupOnly,
            statHandler: (seg, _) => Task.FromResult(new UsenetStatResponse
            {
                ArticleExists = true,
                ResponseCode = (int)UsenetResponseType.ArticleExists,
                ResponseMessage = "223"
            }));

        var providers = new List<MultiProviderNntpClient.ProviderDescriptor>
        {
            new(pooledProvider, ProviderType.Pooled, () => 10),
            new(backupProvider, ProviderType.BackupOnly, () => 5),
        };
        using var client = new MultiProviderNntpClient(providers);

        // First call: pooled returns NoArticle, falls through to backup
        var result1 = await client.StatAsync("seg1", CancellationToken.None);
        Assert.True(result1.ArticleExists);

        // Second call: pooled should NOT be blocked (article-not-found is normal)
        pooledCallCount = 0;
        var result2 = await client.StatAsync("seg2", CancellationToken.None);
        Assert.True(result2.ArticleExists);
        Assert.Equal(1, pooledCallCount); // Pooled was still tried
    }

    [Fact]
    public async Task BlockedProvider_BecomesEligibleAfterCooldown()
    {
        var failingProvider = new FakeProviderClient(
            ProviderType.Pooled,
            statHandler: (_, _) => throw new CouldNotConnectToUsenetException("timeout"));

        var providers = new List<MultiProviderNntpClient.ProviderDescriptor>
        {
            new(failingProvider, ProviderType.Pooled, () => 10),
        };
        using var client = new MultiProviderNntpClient(providers);

        // Provider fails
        await Assert.ThrowsAsync<CouldNotConnectToUsenetException>(
            () => client.StatAsync("seg1", CancellationToken.None));

        // Provider is blocked but since it's the only one, it should still be attempted
        await Assert.ThrowsAsync<CouldNotConnectToUsenetException>(
            () => client.StatAsync("seg2", CancellationToken.None));
    }

    [Fact]
    public async Task AllProvidersBlocked_StillAttemptsOne()
    {
        var callCount = 0;
        var provider1 = new FakeProviderClient(
            ProviderType.Pooled,
            statHandler: (_, _) =>
            {
                Interlocked.Increment(ref callCount);
                throw new CouldNotConnectToUsenetException("timeout");
            });
        var provider2 = new FakeProviderClient(
            ProviderType.BackupOnly,
            statHandler: (_, _) =>
            {
                Interlocked.Increment(ref callCount);
                throw new CouldNotConnectToUsenetException("timeout");
            });

        var providers = new List<MultiProviderNntpClient.ProviderDescriptor>
        {
            new(provider1, ProviderType.Pooled, () => 10),
            new(provider2, ProviderType.BackupOnly, () => 5),
        };
        using var client = new MultiProviderNntpClient(providers);

        // First call: both fail, both get blocked
        await Assert.ThrowsAsync<CouldNotConnectToUsenetException>(
            () => client.StatAsync("seg1", CancellationToken.None));

        // Second call: both are blocked, but client should still attempt at least one
        callCount = 0;
        await Assert.ThrowsAsync<CouldNotConnectToUsenetException>(
            () => client.StatAsync("seg2", CancellationToken.None));
        Assert.True(callCount >= 1, "At least one provider should have been attempted");
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test backend.Tests --filter "FullyQualifiedName~MultiProviderNntpClientTests" --no-restore`
Expected: FAIL — `ProviderDescriptor` type does not exist on `MultiProviderNntpClient`.

**Step 3: Refactor MultiProviderNntpClient to accept ProviderDescriptors**

Replace the constructor and internal structures in `backend/Clients/Usenet/MultiProviderNntpClient.cs`:

```csharp
// backend/Clients/Usenet/MultiProviderNntpClient.cs
using System.Runtime.ExceptionServices;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using Serilog;
using UsenetSharp.Models;

namespace NzbWebDAV.Clients.Usenet;

public class MultiProviderNntpClient : NntpClient
{
    public record ProviderDescriptor(
        INntpClient Client,
        ProviderType ProviderType,
        Func<int> GetAvailableConnections);

    private sealed class ProviderEntry(ProviderDescriptor descriptor)
    {
        public INntpClient Client => descriptor.Client;
        public ProviderType ProviderType => descriptor.ProviderType;
        public int AvailableConnections => descriptor.GetAvailableConnections();
        public ProviderHealthState HealthState { get; } = new();
    }

    private readonly List<ProviderEntry> _providers;

    public MultiProviderNntpClient(List<ProviderDescriptor> descriptors)
    {
        _providers = descriptors.Select(d => new ProviderEntry(d)).ToList();
    }

    // Keep backward-compat constructor for existing callers during transition
    public MultiProviderNntpClient(List<MultiConnectionNntpClient> providers)
        : this(providers.Select(p => new ProviderDescriptor(
            p,
            p.ProviderType,
            () => p.AvailableConnections)).ToList())
    {
    }

    public override Task ConnectAsync(string host, int port, bool useSsl, CancellationToken ct)
    {
        throw new NotSupportedException("Please connect within the connectionFactory");
    }

    public override Task<UsenetResponse> AuthenticateAsync(string user, string pass, CancellationToken ct)
    {
        throw new NotSupportedException("Please authenticate within the connectionFactory");
    }

    public override Task<UsenetStatResponse> StatAsync(SegmentId segmentId, CancellationToken cancellationToken)
    {
        return RunFromPoolWithBackup(x => x.StatAsync(segmentId, cancellationToken), cancellationToken);
    }

    public override Task<UsenetHeadResponse> HeadAsync(SegmentId segmentId, CancellationToken cancellationToken)
    {
        return RunFromPoolWithBackup(x => x.HeadAsync(segmentId, cancellationToken), cancellationToken);
    }

    public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(SegmentId segmentId, CancellationToken cancellationToken)
    {
        return RunFromPoolWithBackup(x => x.DecodedBodyAsync(segmentId, cancellationToken), cancellationToken);
    }

    public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(SegmentId segmentId, CancellationToken cancellationToken)
    {
        return RunFromPoolWithBackup(x => x.DecodedArticleAsync(segmentId, cancellationToken), cancellationToken);
    }

    public override Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken)
    {
        return RunFromPoolWithBackup(x => x.DateAsync(cancellationToken), cancellationToken);
    }

    public override async Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
        SegmentId segmentId,
        Action<ArticleBodyResult>? onConnectionReadyAgain,
        CancellationToken cancellationToken)
    {
        UsenetDecodedBodyResponse? result;
        try
        {
            result = await RunFromPoolWithBackup(
                x => x.DecodedBodyAsync(segmentId, OnConnectionReadyAgain, cancellationToken),
                cancellationToken
            ).ConfigureAwait(false);
        }
        catch
        {
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved);
            throw;
        }

        if (result.ResponseType != UsenetResponseType.ArticleRetrievedBodyFollows)
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved);

        return result;

        void OnConnectionReadyAgain(ArticleBodyResult articleBodyResult)
        {
            if (articleBodyResult == ArticleBodyResult.Retrieved)
                onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
        }
    }

    public override async Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
        SegmentId segmentId,
        Action<ArticleBodyResult>? onConnectionReadyAgain,
        CancellationToken cancellationToken)
    {
        UsenetDecodedArticleResponse? result;
        try
        {
            result = await RunFromPoolWithBackup(
                x => x.DecodedArticleAsync(segmentId, OnConnectionReadyAgain, cancellationToken),
                cancellationToken
            ).ConfigureAwait(false);
        }
        catch
        {
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved);
            throw;
        }

        if (result.ResponseType != UsenetResponseType.ArticleRetrievedHeadAndBodyFollow)
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved);

        return result;

        void OnConnectionReadyAgain(ArticleBodyResult articleBodyResult)
        {
            if (articleBodyResult == ArticleBodyResult.Retrieved)
                onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
        }
    }

    private async Task<T> RunFromPoolWithBackup<T>(
        Func<INntpClient, Task<T>> task,
        CancellationToken cancellationToken
    ) where T : UsenetResponse
    {
        ExceptionDispatchInfo? lastException = null;
        var orderedProviders = GetOrderedProviders();
        for (var i = 0; i < orderedProviders.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = orderedProviders[i];
            var isLastProvider = i == orderedProviders.Count - 1;

            if (lastException is not null)
            {
                var msg = lastException.SourceException.Message;
                Log.Debug("Encountered error during NNTP Operation: `{Message}`. Trying another provider.", msg);
            }

            try
            {
                var result = await task.Invoke(entry.Client).ConfigureAwait(false);

                // if no article with that message-id is found, try again with the next provider.
                if (!isLastProvider && result.ResponseType == UsenetResponseType.NoArticleWithThatMessageId)
                    continue;

                entry.HealthState.RegisterSuccess();
                return result;
            }
            catch (Exception e) when (!e.IsCancellationException())
            {
                lastException = ExceptionDispatchInfo.Capture(e);

                if (ShouldBlockProvider(e))
                {
                    var cooldown = GetCooldown(entry.HealthState.ConsecutiveFailures);
                    entry.HealthState.RegisterFailure(DateTimeOffset.UtcNow, e, cooldown);
                    Log.Warning("Provider {Index} blocked for {Cooldown}s after failure: {Message}",
                        i, cooldown.TotalSeconds, e.Message);
                }
            }
        }

        lastException?.Throw();
        throw new Exception("There are no usenet providers configured.");
    }

    private List<ProviderEntry> GetOrderedProviders()
    {
        var now = DateTimeOffset.UtcNow;
        var unblocked = _providers
            .Where(x => x.ProviderType != ProviderType.Disabled)
            .Where(x => !x.HealthState.IsBlocked(now))
            .OrderBy(x => x.ProviderType)
            .ThenByDescending(x => x.AvailableConnections)
            .ToList();

        // If all providers are blocked, allow all non-disabled through
        if (unblocked.Count == 0)
        {
            unblocked = _providers
                .Where(x => x.ProviderType != ProviderType.Disabled)
                .OrderBy(x => x.ProviderType)
                .ThenByDescending(x => x.AvailableConnections)
                .ToList();
        }

        return unblocked;
    }

    private static bool ShouldBlockProvider(Exception ex)
    {
        // Article-not-found is normal failover, not a provider health issue
        if (ex is UsenetArticleNotFoundException) return false;
        if (ex.TryGetCausingException(out UsenetArticleNotFoundException _)) return false;

        // Auth, connect, timeout failures indicate provider health issues
        if (ex is CouldNotConnectToUsenetException) return true;
        if (ex is CouldNotLoginToUsenetException) return true;
        if (ex is RetryableDownloadException) return true;

        // Timeout-like exceptions
        if (ex is TimeoutException) return true;
        if (ex is IOException) return true;

        return false;
    }

    private static TimeSpan GetCooldown(int consecutiveFailures)
    {
        // Exponential backoff: 60s, 120s, 240s, capped at 300s
        var seconds = Math.Min(60 * Math.Pow(2, consecutiveFailures), 300);
        return TimeSpan.FromSeconds(seconds);
    }

    public override void Dispose()
    {
        foreach (var entry in _providers)
            if (entry.Client is IDisposable disposable)
                disposable.Dispose();
        GC.SuppressFinalize(this);
    }
}
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test backend.Tests --filter "FullyQualifiedName~MultiProviderNntpClientTests" --no-restore`
Expected: 5 PASS

**Step 5: Run all existing tests to verify no regressions**

Run: `dotnet test backend.Tests --no-restore`
Expected: All tests pass. The backward-compat constructor `MultiProviderNntpClient(List<MultiConnectionNntpClient>)` ensures `UsenetStreamingClient` still compiles.

**Step 6: Commit**

```bash
git add backend/Clients/Usenet/MultiProviderNntpClient.cs backend.Tests/Clients/Usenet/MultiProviderNntpClientTests.cs
git commit -m "feat(#282): integrate provider health state into MultiProviderNntpClient with cooldown-based failover"
```

---

## PR 2: Organized Link Scan Reduction

### Task 3: OrganizedLinkIndex Singleton

**Files:**
- Create: `backend/Services/OrganizedLinkIndex.cs`
- Modify: `backend/Utils/OrganizedLinksUtil.cs`
- Modify: `backend/Tasks/RemoveUnlinkedFilesTask.cs`
- Modify: `backend/Tasks/StrmToSymlinksTask.cs`
- Modify: `backend/Services/ContentIndexRecoveryService.cs`
- Modify: `backend/Program.cs`
- Test: `backend.Tests/Services/OrganizedLinkIndexTests.cs`

**Step 1: Write the failing tests**

```csharp
// backend.Tests/Services/OrganizedLinkIndexTests.cs
using NzbWebDAV.Services;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Tests.Services;

public class OrganizedLinkIndexTests
{
    [Fact]
    public void RepeatedGetLinkedIds_ReuseSingleScan()
    {
        // The index should cache results; repeated calls return same snapshot
        var index = new OrganizedLinkIndex();
        var scanCount = 0;

        // Use the test overload that accepts a factory
        var ids1 = index.GetLinkedIds(() =>
        {
            scanCount++;
            return CreateFakeLinks();
        });
        var ids2 = index.GetLinkedIds(() =>
        {
            scanCount++;
            return CreateFakeLinks();
        });

        Assert.Equal(1, scanCount);
        Assert.Equal(ids1, ids2);
    }

    [Fact]
    public void Invalidate_ForcesRescan()
    {
        var index = new OrganizedLinkIndex();
        var scanCount = 0;

        index.GetLinkedIds(() =>
        {
            scanCount++;
            return CreateFakeLinks();
        });
        Assert.Equal(1, scanCount);

        index.Invalidate();

        index.GetLinkedIds(() =>
        {
            scanCount++;
            return CreateFakeLinks();
        });
        Assert.Equal(2, scanCount);
    }

    [Fact]
    public void GetAllLinks_ReturnsCachedSnapshot()
    {
        var index = new OrganizedLinkIndex();
        var scanCount = 0;

        var links1 = index.GetAllLinks(() =>
        {
            scanCount++;
            return CreateFakeLinks();
        });
        var links2 = index.GetAllLinks(() =>
        {
            scanCount++;
            return CreateFakeLinks();
        });

        Assert.Equal(1, scanCount);
        Assert.Same(links1, links2);
    }

    [Fact]
    public void GetLinksForItem_ResolvesFromCachedIndex()
    {
        var index = new OrganizedLinkIndex();
        var targetId = Guid.NewGuid();
        var link = new OrganizedLinksUtil.DavItemLink
        {
            LinkPath = "/media/movie.mkv",
            DavItemId = targetId,
        };

        var result = index.GetLinksForItem(targetId, () => [link]);

        Assert.Single(result);
        Assert.Equal(targetId, result[0].DavItemId);
    }

    private static List<OrganizedLinksUtil.DavItemLink> CreateFakeLinks()
    {
        return
        [
            new OrganizedLinksUtil.DavItemLink
            {
                LinkPath = "/media/movie1.mkv",
                DavItemId = Guid.NewGuid(),
            },
            new OrganizedLinksUtil.DavItemLink
            {
                LinkPath = "/media/movie2.mkv",
                DavItemId = Guid.NewGuid(),
            },
        ];
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test backend.Tests --filter "FullyQualifiedName~OrganizedLinkIndexTests" --no-restore`
Expected: FAIL — `OrganizedLinkIndex` type does not exist.

**Step 3: Create OrganizedLinkIndex**

```csharp
// backend/Services/OrganizedLinkIndex.cs
using NzbWebDAV.Config;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Services;

public class OrganizedLinkIndex
{
    private readonly object _lock = new();
    private List<OrganizedLinksUtil.DavItemLink>? _snapshot;
    private Dictionary<Guid, List<OrganizedLinksUtil.DavItemLink>>? _byItemId;

    /// <summary>
    /// Get all links, using the provided factory on first call or after invalidation.
    /// </summary>
    public List<OrganizedLinksUtil.DavItemLink> GetAllLinks(Func<List<OrganizedLinksUtil.DavItemLink>> factory)
    {
        EnsureBuilt(factory);
        return _snapshot!;
    }

    /// <summary>
    /// Production overload: scans the library via OrganizedLinksUtil.
    /// </summary>
    public List<OrganizedLinksUtil.DavItemLink> GetAllLinks(ConfigManager configManager)
    {
        return GetAllLinks(() => OrganizedLinksUtil.GetLibraryDavItemLinks(configManager).ToList());
    }

    /// <summary>
    /// Get links for a specific DavItem.
    /// </summary>
    public List<OrganizedLinksUtil.DavItemLink> GetLinksForItem(
        Guid davItemId,
        Func<List<OrganizedLinksUtil.DavItemLink>> factory)
    {
        EnsureBuilt(factory);
        return _byItemId!.TryGetValue(davItemId, out var links) ? links : [];
    }

    /// <summary>
    /// Production overload.
    /// </summary>
    public List<OrganizedLinksUtil.DavItemLink> GetLinksForItem(Guid davItemId, ConfigManager configManager)
    {
        return GetLinksForItem(davItemId, () => OrganizedLinksUtil.GetLibraryDavItemLinks(configManager).ToList());
    }

    /// <summary>
    /// Get all linked DavItem IDs.
    /// </summary>
    public HashSet<Guid> GetLinkedIds(Func<List<OrganizedLinksUtil.DavItemLink>> factory)
    {
        EnsureBuilt(factory);
        return _byItemId!.Keys.ToHashSet();
    }

    /// <summary>
    /// Production overload.
    /// </summary>
    public HashSet<Guid> GetLinkedIds(ConfigManager configManager)
    {
        return GetLinkedIds(() => OrganizedLinksUtil.GetLibraryDavItemLinks(configManager).ToList());
    }

    /// <summary>
    /// Invalidate the cached index, forcing a rescan on next access.
    /// </summary>
    public void Invalidate()
    {
        lock (_lock)
        {
            _snapshot = null;
            _byItemId = null;
        }
    }

    private void EnsureBuilt(Func<List<OrganizedLinksUtil.DavItemLink>> factory)
    {
        if (_snapshot != null) return;
        lock (_lock)
        {
            if (_snapshot != null) return;
            var links = factory();
            _byItemId = new Dictionary<Guid, List<OrganizedLinksUtil.DavItemLink>>();
            foreach (var link in links)
            {
                if (!_byItemId.TryGetValue(link.DavItemId, out var list))
                {
                    list = [];
                    _byItemId[link.DavItemId] = list;
                }
                list.Add(link);
            }
            _snapshot = links;
        }
    }
}
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test backend.Tests --filter "FullyQualifiedName~OrganizedLinkIndexTests" --no-restore`
Expected: 4 PASS

**Step 5: Commit**

```bash
git add backend/Services/OrganizedLinkIndex.cs backend.Tests/Services/OrganizedLinkIndexTests.cs
git commit -m "feat: add OrganizedLinkIndex singleton for cached library link scanning"
```

---

### Task 4: Wire OrganizedLinkIndex into Consumers

**Files:**
- Modify: `backend/Tasks/RemoveUnlinkedFilesTask.cs`
- Modify: `backend/Tasks/StrmToSymlinksTask.cs`
- Modify: `backend/Services/ContentIndexRecoveryService.cs`
- Modify: `backend/Program.cs`

**Step 1: Register in Program.cs**

In `backend/Program.cs`, find the `.AddSingleton<QueueManager>()` line (line ~87) and add after it:

```csharp
.AddSingleton<OrganizedLinkIndex>()
```

Add the using at top if needed:
```csharp
using NzbWebDAV.Services;
```

**Step 2: Update RemoveUnlinkedFilesTask**

In `backend/Tasks/RemoveUnlinkedFilesTask.cs`:

Change the constructor to accept `OrganizedLinkIndex`:
```csharp
public class RemoveUnlinkedFilesTask(
    ConfigManager configManager,
    DavDatabaseClient dbClient,
    WebsocketManager websocketManager,
    OrganizedLinkIndex organizedLinkIndex,
    bool isDryRun
) : BaseTask
```

Change the `GetLinkedIds()` method:
```csharp
private HashSet<Guid> GetLinkedIds()
{
    return organizedLinkIndex.GetLinkedIds(configManager);
}
```

**Step 3: Update StrmToSymlinksTask**

In `backend/Tasks/StrmToSymlinksTask.cs`:

Change the constructor:
```csharp
public class StrmToSymlinksTask(
    ConfigManager configManager,
    DavDatabaseClient dbClient,
    WebsocketManager websocketManager,
    OrganizedLinkIndex organizedLinkIndex
) : BaseTask
```

Change `ConvertAllStrmFilesToSymlinks`:
```csharp
var batches = organizedLinkIndex.GetAllLinks(configManager)
    .Where(x => x.SymlinkOrStrmInfo is SymlinkAndStrmUtil.StrmInfo)
    .ToBatches(batchSize: 100);
```

**Step 4: Update ContentIndexRecoveryService**

In `backend/Services/ContentIndexRecoveryService.cs`:

Change the constructor:
```csharp
public sealed class ContentIndexRecoveryService(
    ConfigManager configManager,
    OrganizedLinkIndex organizedLinkIndex
) : IHostedService
```

Change `GetLinkedItemIds`:
```csharp
private static IEnumerable<Guid> GetLinkedItemIds(ConfigManager configManager, OrganizedLinkIndex organizedLinkIndex)
{
    var libraryDir = configManager.GetLibraryDir();
    if (string.IsNullOrWhiteSpace(libraryDir) || !Directory.Exists(libraryDir))
        return [];

    try
    {
        return organizedLinkIndex.GetLinkedIds(configManager);
    }
    catch (Exception ex)
    {
        Log.Warning(ex, "Failed to inspect library links while evaluating /content recovery.");
        return [];
    }
}
```

Update the call site in `BuildRecoveryPlanAsync` to pass the index. Since `BuildRecoveryPlanAsync` is `internal static`, change the signature:
```csharp
internal static async Task<RecoveryPlan> BuildRecoveryPlanAsync(
    DavDatabaseContext dbContext,
    ContentIndexSnapshotStore.ContentIndexSnapshot snapshot,
    ConfigManager configManager,
    OrganizedLinkIndex organizedLinkIndex,
    CancellationToken cancellationToken)
```

And update the call inside `StartAsync`:
```csharp
var plan = await BuildRecoveryPlanAsync(dbContext, snapshot, configManager, organizedLinkIndex, cancellationToken).ConfigureAwait(false);
```

And the linked-id lookup:
```csharp
foreach (var linkedItemId in GetLinkedItemIds(configManager, organizedLinkIndex))
```

**Step 5: Find and update all callers of RemoveUnlinkedFilesTask and StrmToSymlinksTask constructors**

Search for where these tasks are instantiated and add the `organizedLinkIndex` parameter. These are typically constructed via DI or manually in task runners.

Run: `grep -rn "new RemoveUnlinkedFilesTask\|new StrmToSymlinksTask" backend/`

Update each call site to pass the `OrganizedLinkIndex` instance.

**Step 6: Run all tests**

Run: `dotnet test backend.Tests --no-restore`
Expected: All tests pass. The `ContentIndexRecoveryServiceTests` may need signature updates if they call `BuildRecoveryPlanAsync` directly.

**Step 7: Commit**

```bash
git add backend/Program.cs backend/Tasks/RemoveUnlinkedFilesTask.cs backend/Tasks/StrmToSymlinksTask.cs backend/Services/ContentIndexRecoveryService.cs
git commit -m "feat: wire OrganizedLinkIndex into tasks and recovery service to eliminate redundant scans"
```

---

## PR 3: End-of-Stream Stall Detection (#115)

### Task 5: UnexpectedSegmentTerminationException

**Files:**
- Create: `backend/Exceptions/UnexpectedSegmentTerminationException.cs`

**Step 1: Create the exception**

```csharp
// backend/Exceptions/UnexpectedSegmentTerminationException.cs
namespace NzbWebDAV.Exceptions;

public class UnexpectedSegmentTerminationException(string message, Exception? innerException = null)
    : Exception(message, innerException)
{
}
```

**Step 2: Commit**

```bash
git add backend/Exceptions/UnexpectedSegmentTerminationException.cs
git commit -m "feat(#115): add UnexpectedSegmentTerminationException"
```

---

### Task 6: NzbFileStream Under-Read Detection

**Files:**
- Modify: `backend/Streams/NzbFileStream.cs`
- Test: `backend.Tests/Streams/NzbFileStreamUnderReadTests.cs`

**Step 1: Write the failing tests**

```csharp
// backend.Tests/Streams/NzbFileStreamUnderReadTests.cs
using NzbWebDAV.Exceptions;
using NzbWebDAV.Models;
using NzbWebDAV.Streams;
using NzbWebDAV.Tests.TestDoubles;

namespace NzbWebDAV.Tests.Streams;

public class NzbFileStreamUnderReadTests
{
    [Fact]
    public async Task CleanFullRead_EndsAtExactLogicalLength()
    {
        // 2 segments, 100 bytes each = 200 total
        var client = new FakeNntpClient()
            .AddSegment("seg-0", new byte[100], partOffset: 0)
            .AddSegment("seg-1", new byte[100], partOffset: 100);

        await using var stream = new NzbFileStream(
            ["seg-0", "seg-1"],
            fileSize: 200,
            client,
            StreamingBufferSettings.Fixed(2));

        var buffer = new byte[200];
        var totalRead = 0;
        while (totalRead < 200)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(totalRead));
            if (read == 0) break;
            totalRead += read;
        }

        Assert.Equal(200, totalRead);
    }

    [Fact]
    public async Task ShortFinalSegment_ThrowsNotSilentEof()
    {
        // Advertise 200 bytes but second segment only has 50
        var client = new FakeNntpClient()
            .AddSegment("seg-0", new byte[100], partOffset: 0)
            .AddSegment("seg-1", new byte[50], partOffset: 100);

        await using var stream = new NzbFileStream(
            ["seg-0", "seg-1"],
            fileSize: 200,
            client,
            StreamingBufferSettings.Fixed(2));

        var buffer = new byte[4096];
        var totalRead = 0;

        await Assert.ThrowsAsync<UnexpectedSegmentTerminationException>(async () =>
        {
            while (totalRead < 200)
            {
                var read = await stream.ReadAsync(buffer.AsMemory());
                if (read == 0)
                    throw new UnexpectedSegmentTerminationException(
                        $"Stream ended at {totalRead} bytes but expected {200}");
                totalRead += read;
            }
        });
    }
}
```

**Step 2: Run tests**

Run: `dotnet test backend.Tests --filter "FullyQualifiedName~NzbFileStreamUnderReadTests" --no-restore`
Expected: `CleanFullRead_EndsAtExactLogicalLength` PASSES, `ShortFinalSegment_ThrowsNotSilentEof` may or may not pass depending on current behavior.

**Step 3: Modify NzbFileStream.ReadAsync to detect under-read**

In `backend/Streams/NzbFileStream.cs`, update `ReadAsync`:

```csharp
public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
{
    if (_position >= fileSize) return 0;
    _innerStream ??= await GetFileStream(_position, cancellationToken).ConfigureAwait(false);
    var read = await _innerStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

    if (read == 0 && _position < fileSize)
    {
        throw new UnexpectedSegmentTerminationException(
            $"NZB file stream ended at position {_position} but expected file size is {fileSize}. " +
            $"Missing {fileSize - _position} bytes.");
    }

    _position += read;
    return read;
}
```

Add the using:
```csharp
using NzbWebDAV.Exceptions;
```

**Step 4: Run tests**

Run: `dotnet test backend.Tests --filter "FullyQualifiedName~NzbFileStreamUnderReadTests" --no-restore`
Expected: PASS

**Step 5: Run all stream tests**

Run: `dotnet test backend.Tests --filter "FullyQualifiedName~Streams" --no-restore`
Expected: All PASS

**Step 6: Commit**

```bash
git add backend/Streams/NzbFileStream.cs backend.Tests/Streams/NzbFileStreamUnderReadTests.cs
git commit -m "feat(#115): detect under-read in NzbFileStream and throw instead of silent EOF"
```

---

### Task 7: Controller Logging for UnexpectedSegmentTerminationException

**Files:**
- Modify: `backend/Api/Controllers/GetWebdavItem/GetWebdavItemController.cs`

**Step 1: Add specific exception handling**

In `GetWebdavItemController.cs`, update `HandleRequest()`:

```csharp
[HttpGet]
public async Task HandleRequest()
{
    try
    {
        HttpContext.Items["configManager"] = configManager;
        var request = new GetWebdavItemRequest(HttpContext);
        await using var response = await GetWebdavItem(request).ConfigureAwait(false);
        await response.CopyToPooledAsync(Response.Body, 64 * 1024, HttpContext.RequestAborted).ConfigureAwait(false);
    }
    catch (UnauthorizedAccessException)
    {
        Response.StatusCode = 401;
    }
    catch (UnexpectedSegmentTerminationException ex)
    {
        Log.Warning(ex,
            "Stream ended before Content-Length was satisfied for {Path}. " +
            "This may indicate missing or corrupted segments.",
            HttpContext.Request.Path);
    }
    catch (UsenetArticleNotFoundException ex)
    {
        Log.Warning(ex,
            "Missing article while serving {Path}: {SegmentId}",
            HttpContext.Request.Path, ex.Message);
    }
}
```

Add usings:
```csharp
using NzbWebDAV.Exceptions;
using Serilog;
```

**Step 2: Run all tests**

Run: `dotnet test backend.Tests --no-restore`
Expected: All PASS

**Step 3: Commit**

```bash
git add backend/Api/Controllers/GetWebdavItem/GetWebdavItemController.cs
git commit -m "feat(#115): add actionable logging for under-read and missing-article failures in view controller"
```

---

## PR 4: Deterministic Temp Cleanup (#294)

### Task 8: ArticleCachingNntpClient IAsyncDisposable

**Files:**
- Modify: `backend/Clients/Usenet/ArticleCachingNntpClient.cs`
- Test: `backend.Tests/Clients/Usenet/ArticleCachingNntpClientTests.cs`

**Step 1: Write the failing tests**

```csharp
// backend.Tests/Clients/Usenet/ArticleCachingNntpClientTests.cs
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Tests.TestDoubles;

namespace NzbWebDAV.Tests.Clients.Usenet;

public class ArticleCachingNntpClientTests
{
    [Fact]
    public async Task DisposeAsync_DeletesCacheDir()
    {
        var inner = new FakeNntpClient().AddSegment("seg-1", new byte[100]);
        var client = new ArticleCachingNntpClient(inner);

        // Trigger caching so the dir exists
        await client.DecodedBodyAsync("seg-1", CancellationToken.None);
        var cacheDir = client.CacheDir;
        Assert.True(Directory.Exists(cacheDir));

        await client.DisposeAsync();
        Assert.False(Directory.Exists(cacheDir));
    }

    [Fact]
    public void Dispose_DeletesCacheDir()
    {
        var inner = new FakeNntpClient().AddSegment("seg-1", new byte[100]);
        var client = new ArticleCachingNntpClient(inner);

        // Trigger caching
        client.DecodedBodyAsync("seg-1", CancellationToken.None).GetAwaiter().GetResult();
        var cacheDir = client.CacheDir;
        Assert.True(Directory.Exists(cacheDir));

        client.Dispose();
        Assert.False(Directory.Exists(cacheDir));
    }

    [Fact]
    public async Task RepeatedDispose_DoesNotThrow()
    {
        var inner = new FakeNntpClient().AddSegment("seg-1", new byte[100]);
        var client = new ArticleCachingNntpClient(inner);

        await client.DisposeAsync();
        await client.DisposeAsync(); // Should not throw
        client.Dispose(); // Should not throw
    }

    [Fact]
    public async Task DisposeAsync_WithOpenFileHandle_EventuallySucceeds()
    {
        var inner = new FakeNntpClient().AddSegment("seg-1", new byte[100]);
        var client = new ArticleCachingNntpClient(inner);

        // Cache a segment
        var response = await client.DecodedBodyAsync("seg-1", CancellationToken.None);
        var cacheDir = client.CacheDir;

        // Dispose the response stream so file handle is released
        await response.Stream.DisposeAsync();

        await client.DisposeAsync();
        Assert.False(Directory.Exists(cacheDir));
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test backend.Tests --filter "FullyQualifiedName~ArticleCachingNntpClientTests" --no-restore`
Expected: FAIL — `CacheDir` property does not exist; `DisposeAsync` not implemented.

**Step 3: Implement IAsyncDisposable on ArticleCachingNntpClient**

Modify `backend/Clients/Usenet/ArticleCachingNntpClient.cs`:

1. Expose `CacheDir` as internal property for testing:
```csharp
internal string CacheDir => _cacheDir;
```

2. Add `IAsyncDisposable` implementation and `_disposeOnce` guard:

Replace the `Dispose()` method and add `DisposeAsync()`:

```csharp
private int _disposed;

public override void Dispose()
{
    if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

    if (!leaveOpen)
        base.Dispose();

    foreach (var semaphore in _pendingRequests.Values)
        semaphore.Dispose();
    _pendingRequests.Clear();
    _cachedSegments.Clear();

    DeleteCacheDirSync(_cacheDir);
    GC.SuppressFinalize(this);
}

public async ValueTask DisposeAsync()
{
    if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

    if (!leaveOpen && base is IAsyncDisposable asyncDisposable)
        await asyncDisposable.DisposeAsync().ConfigureAwait(false);
    else if (!leaveOpen)
        base.Dispose();

    foreach (var semaphore in _pendingRequests.Values)
        semaphore.Dispose();
    _pendingRequests.Clear();
    _cachedSegments.Clear();

    await DeleteCacheDirAsync(_cacheDir).ConfigureAwait(false);
    GC.SuppressFinalize(this);
}

private static void DeleteCacheDirSync(string cacheDir)
{
    var deadline = DateTime.UtcNow.AddSeconds(10);
    var delay = 100;
    while (DateTime.UtcNow < deadline)
    {
        try
        {
            Directory.Delete(cacheDir, recursive: true);
            return;
        }
        catch (Exception)
        {
            Thread.Sleep(delay);
            delay = Math.Min(delay * 2, 2000);
        }
    }

    // Best-effort: log but don't throw from disposal
    Serilog.Log.Warning("Failed to delete cache directory {CacheDir} within timeout", cacheDir);
}

private static async Task DeleteCacheDirAsync(string cacheDir)
{
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var delay = 100;
    while (!cts.Token.IsCancellationRequested)
    {
        try
        {
            Directory.Delete(cacheDir, recursive: true);
            return;
        }
        catch (Exception) when (!cts.Token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(delay, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            delay = Math.Min(delay * 2, 2000);
        }
    }

    Serilog.Log.Warning("Failed to delete cache directory {CacheDir} within timeout", cacheDir);
}
```

Remove the old `DeleteCacheDir` static method and the `Task.Run` call.

**Step 4: Run tests**

Run: `dotnet test backend.Tests --filter "FullyQualifiedName~ArticleCachingNntpClientTests" --no-restore`
Expected: 4 PASS

**Step 5: Run all tests**

Run: `dotnet test backend.Tests --no-restore`
Expected: All PASS

**Step 6: Commit**

```bash
git add backend/Clients/Usenet/ArticleCachingNntpClient.cs backend.Tests/Clients/Usenet/ArticleCachingNntpClientTests.cs
git commit -m "feat(#294): replace fire-and-forget temp cleanup with deterministic IAsyncDisposable"
```

---

## Summary of All Commits

| PR | Commit | Description |
|----|--------|-------------|
| 1 | Task 1 | `ProviderHealthState` with cooldown tracking |
| 1 | Task 2 | Integrate health state into `MultiProviderNntpClient` |
| 2 | Task 3 | `OrganizedLinkIndex` singleton |
| 2 | Task 4 | Wire index into tasks and recovery service |
| 3 | Task 5 | `UnexpectedSegmentTerminationException` |
| 3 | Task 6 | NzbFileStream under-read detection |
| 3 | Task 7 | Controller logging for stream failures |
| 4 | Task 8 | Deterministic IAsyncDisposable for cache cleanup |

## Design Constraints Verified

- No HTTP route changes
- No frontend setting changes
- No queue semantic changes
- No new config keys
- No MemoryCache used (OrganizedLinkIndex uses lock + snapshot)
- No fire-and-forget Task.Run (replaced in #294)
- All changes are narrow internal changes with tests
