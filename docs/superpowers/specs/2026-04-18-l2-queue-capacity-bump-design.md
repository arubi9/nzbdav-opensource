# L2 write queue capacity bump — design

## Problem

`cache.l2.write-queue-capacity` default is 2048. At 5 active streams,
queue pinned at cap within seconds of burst load, 731 writes dropped in
a 5-second sample.

Queue full means `EnqueueWrite` increments `_l2WritesDropped` and
returns without queueing. The segment is NOT cached in L2. On next
request for that segment, full NNTP miss path re-fetches — multiplying
NNTP load and user-visible cold-start cost.

## Why low default exists today

Queue memory cost = capacity × avg `WriteRequest` size.
`WriteRequest` holds `byte[] Body` which is the full segment body
(~750 KB). At cap=2048 × 750 KB = ~1.5 GB heap usage worst case
(when queue full).

Original author picked 2048 as a safe-for-small-RAM default.

## Proposed change

Raise default to **16384** and make it advisory (capacity is byte-count
aware if later needed).

Memory impact: 16384 × 750 KB = ~12 GB worst case. Server B has 22 GB RAM,
Hetzner EX44 has 64 GB. Headroom exists on both.

Practical memory usage is much lower because:
- Queue fills only during burst; drains between bursts
- Average queue depth in healthy state = <100
- Worst case only during cold-library thundering herd

## Config surface

`ConfigManager.GetL2WriteQueueCapacity()` already exists. Just change
the default:

```csharp
public int GetL2WriteQueueCapacity()
    => int.Parse(StringUtil.EmptyToNull(GetConfigValue("cache.l2.write-queue-capacity")) ?? "16384");
```

## Memory-safety guard (optional follow-up)

Adding `MaxQueuedBytes` limit alongside capacity would be more precise
but is not in scope for this change. Current approach: trust the
operator to size the queue for their RAM, surface it via config.

## Verification

- **Unit test:** `CtorFromConfig_UsesConfiguredCapacity` — set to 8192,
  assert `EnqueueWrite` stops dropping until 8193rd write.
- **Live:** deploy to Server B, watch `queue_depth` climb during
  playback. Confirm drop rate falls toward zero once workers catch up.

## Interaction with multi-threaded writer

Pairs with that spec. Bigger queue absorbs bursts; parallel writers
drain faster. Either alone leaves 50% of the problem. Together they
eliminate drops under realistic loads.

## Rollback

Single config value. Revert by setting
`cache.l2.write-queue-capacity = 2048` without redeploy.
