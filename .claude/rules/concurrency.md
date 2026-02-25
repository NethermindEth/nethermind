---
paths:
  - "src/Nethermind/**/*.cs"
---

# Concurrency Patterns

Nethermind is a multi-threaded process with concurrent block processing, networking, and state management. Incorrect use of shared state will cause data races, stale reads, and non-determinism — bugs that are extremely hard to reproduce and catastrophic in production.

## Volatile.Read / Volatile.Write

- **Always** use `Volatile.Read(ref field)` when reading a field that is written by another thread via `Interlocked.*`, even inside `while`/`for` loop conditions. Without it the JIT is free to hoist the read outside the loop and the thread will never observe the update.
- Use `Volatile.Write(ref field, value)` for single-writer flags (e.g. `_stopped`, `_disposed`). The paired read must also use `Volatile.Read`.

```csharp
// Wrong — JIT may cache `_running` and loop forever
while (_running)
    DoWork();

// Correct
while (Volatile.Read(ref _running))
    DoWork();
```

## Interlocked

- Use `Interlocked.Exchange` / `CompareExchange` for lock-free atomic read-modify-write.
- **Never** read a field with plain access in loop conditions or branch tests when another thread writes it via `Interlocked.*`. Use `Volatile.Read` or `Interlocked.CompareExchange(ref field, null, null)` as a zero-cost read barrier:

```csharp
// Read barrier via no-op CAS (equivalent to Volatile.Read for reference types)
string current = Interlocked.CompareExchange(ref _field, null, null);
```

- Do not mix `Interlocked` writes and `Volatile` writes on the same field — pick one.

## lock vs Interlocked

- Use `Interlocked` for single-field atomic updates (counters, flags, single reference swaps).
- Use `lock` when the update involves two or more fields that must be consistent with each other, or when the critical section is non-trivial.

```csharp
// Multi-field: must lock
lock (_sync)
{
    _count++;
    _lastValue = value;
}

// Single atomic counter: Interlocked is sufficient
Interlocked.Increment(ref _count);
```

## Thread-safe initialization

Prefer `Lazy<T>` for expensive singletons. If manual double-checked locking is necessary, the backing field **must** be `volatile`:

```csharp
private volatile ICache _cache;

ICache GetCache()
{
    if (_cache is not null) return _cache;
    lock (_lock)
    {
        if (_cache is not null) return _cache;
        _cache = BuildCache();
    }
    return _cache;
}
```

## Cancellation

- Check `CancellationToken.IsCancellationRequested` via `Volatile.Read` semantics — the runtime already does this internally, so a plain `.IsCancellationRequested` read is safe.
- Prefer `ThrowIfCancellationRequested()` at loop boundaries over manual `if` checks to get consistent exception types.

## SeqlockCache and similar

- `SeqlockCache` uses a seqlock protocol — reads must retry if the sequence number changed during the read. Do not break the retry loop by reading the sequence number with a plain field access.
- Struct copies inside a seqlock critical section are intentionally large — do not "optimize" away the copy by using `ref` returns without understanding the seqlock protocol.
