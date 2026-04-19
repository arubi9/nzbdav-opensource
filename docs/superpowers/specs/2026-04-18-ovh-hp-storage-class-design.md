# OVH High Performance Object Storage (EXPRESS_ONEZONE) — design

## Problem

`ObjectStorageSegmentCache.CreateWriteDelegate` writes L2 objects to
OVH's S3-compatible bucket without specifying a storage class, so
every PUT lands on the Standard tier. OVH offers a High Performance
(HP) tier mapped to the AWS `EXPRESS_ONEZONE` class that delivers
3-5× lower PUT/GET latency (50-150 ms vs 300-800 ms on Standard) and
eliminates the long-tail S3 latency spikes (500-3000 ms p99) that
contribute to the 5-second max TTFB observed under the 2026-04-18
120-concurrent stress test.

Egress is zero on OVH Object Storage as of 2026-01-01, so the only
cost delta between Standard and HP is storage price (~+$1-3/mo at
our scale).

## Goal

Let operators opt NZBDAV's L2 writes into OVH's HP tier via a single
config key, without endpoint or client library changes. Same bucket,
same access keys, same Minio client — just an extra header on PUT.

## Config

New ConfigManager getter:

```csharp
public string GetL2StorageClass()
    => StringUtil.EmptyToNull(GetConfigValue("cache.l2.storage-class"))?.ToUpperInvariant()
       ?? "STANDARD";
```

Valid values: `STANDARD` (default), `EXPRESS_ONEZONE`, `STANDARD_IA`,
`GLACIER_IR`. We only test STANDARD + EXPRESS_ONEZONE; other AWS
tiers pass through if an operator wants them.

## Code change

`ObjectStorageSegmentCache.CreateWriteDelegate` adds the header to
the `headers` dictionary it already builds:

```csharp
if (!string.Equals(storageClass, "STANDARD", StringComparison.OrdinalIgnoreCase))
    headers["x-amz-storage-class"] = storageClass;
```

Wire the storage class through the constructor via `ConfigBinding`
(already holds other config-driven values like read/write timeouts,
queue capacity, writer parallelism). Default is `"STANDARD"` — any
existing callers that use the non-config ctor get legacy behaviour
unchanged.

## Behaviour per storage class

| Value | x-amz-storage-class header | Result |
|---|---|---|
| `STANDARD` (default) | not sent | OVH Standard tier (current behaviour) |
| `EXPRESS_ONEZONE` | `EXPRESS_ONEZONE` | OVH High Performance tier |
| `STANDARD_IA` | `STANDARD_IA` | OVH Infrequent Access (cheaper, higher retrieval latency) |

We omit the header on STANDARD so requests remain byte-compatible
with the Minio bucket-default routing. Writing the header with any
non-default value explicitly overrides.

## Scope

**In scope**
1. `ConfigManager.GetL2StorageClass()` — new getter.
2. `ObjectStorageSegmentCache` constructor chain — accept
   `storageClass` param, default `"STANDARD"`.
3. `ConfigBinding` record — new `StorageClass` field.
4. `CreateFromConfig` — read config, pass into binding.
5. `CreateWriteDelegate` — inject header conditional on non-default.
6. Unit test — verify:
   (a) default ctor omits header, and
   (b) `storageClass = "EXPRESS_ONEZONE"` injects the header with
   exact value.

**Out of scope**
- Migrating existing objects from Standard to HP. Done separately
  via `aws s3api copy-object` sweep.
- Reading `x-amz-storage-class` back on GET. OVH routes transparently.
- Per-category class routing (e.g. write SmallFile to HP, VideoSegment
  to Standard). Follow-up if operators need it.

## Verification

**Unit test (new)**
```csharp
[Fact]
public async Task StorageClass_EXPRESS_ONEZONE_InjectsHeader()
{
    IReadOnlyDictionary<string, string>? observedHeaders = null;

    using var cache = new ObjectStorageSegmentCache(
        bucketName: "b",
        queueCapacity: 4,
        ensureBucketExistsAsync: _ => Task.CompletedTask,
        tryReadAsync: (_, _) => Task.FromResult<ObjectStorageSegmentCache.ReadResult?>(null),
        writeAsync: (req, _) =>
        {
            // CreateWriteDelegate adds the header; here we inject an assertion
            // inside the delegate — the test rigs the delegate manually below.
            return Task.CompletedTask;
        });
    // See actual implementation — test the internal write-delegate
    // building path, not just the public ctor.
    // Prefer: expose a static helper or integration-test against a real
    // Minio endpoint in the existing ObjectStorageIntegrationTests suite.
}
```

The simpler test: verify `CreateFromConfig` passes the configured
value into the ConfigBinding, and that `CreateWriteDelegate` adds
the header when the binding's storage class != STANDARD. If the
existing code structure doesn't expose those internals, expose a
`BuildWriteHeaders(...)` internal static helper and test that
directly with various storage-class inputs.

Acceptable test minimum: one that asserts the header is present in
the dictionary passed to PutObjectArgs when storage class is
EXPRESS_ONEZONE, and absent when STANDARD.

**Build**
```
cd backend && dotnet build --nologo -v q
```
Must return `0 Error(s)`.

**Tests**
```
dotnet test backend.Tests --nologo --no-build --filter 'FullyQualifiedName~ObjectStorageSegmentCacheTests'
```
All existing cache tests plus the new one must pass.

**Full suite**
```
dotnet test backend.Tests --nologo --no-build
```
Zero failures.

## Migration of existing objects (ops, not code)

After deploying the code change and setting the config, run this
one-time sweep on the OVH bucket to promote existing Standard-tier
objects to EXPRESS_ONEZONE:

```bash
# On Server B (needs aws CLI + S3 creds)
aws configure set default.s3.max_concurrent_requests 32
aws s3api list-objects-v2 \
  --bucket nzbdav-cache \
  --prefix segments/ \
  --endpoint-url https://s3.us-east-va.io.cloud.ovh.us \
  --query 'Contents[].Key' --output text | tr '\t' '\n' | \
xargs -P 16 -I{} aws s3api copy-object \
  --bucket nzbdav-cache \
  --key {} \
  --copy-source nzbdav-cache/{} \
  --storage-class EXPRESS_ONEZONE \
  --metadata-directive COPY \
  --endpoint-url https://s3.us-east-va.io.cloud.ovh.us
```

Server-side copy, no bandwidth egress (intra-DC). ~60 GB at Standard
→ HP promotion takes a few minutes.

New writes land on HP from the moment config + code are deployed —
migration only affects the already-present Standard-tier backlog.

## Rollback

Set config back to `STANDARD` and restart NZBDAV. New writes revert
to Standard. Existing HP objects stay HP (reads work transparently;
small extra storage cost until they're rewritten via normal churn).

## Expected impact

| Metric | Standard (now) | HP |
|---|---|---|
| L2 PUT p50 | 300-800 ms | 50-150 ms |
| L2 GET p50 | 300-600 ms | 30-100 ms |
| Writer pool throughput (4 workers) | ~13/sec | 40-60/sec |
| P99 TTFB under 120-concurrent load | ~5 s | ~2.5 s |
| Storage cost at 60 GB | $0.70/mo | $1.80/mo |

Zero egress fee on either tier so no bandwidth cost delta.
