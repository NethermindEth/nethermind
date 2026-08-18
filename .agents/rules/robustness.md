# C#/.NET Robustness

Patterns that cause silent failures, resource leaks, or deadlocks in production. These apply to all C# code in the repo.

## Async

- **Never** use `async void` — exceptions are silently swallowed. Use `async Task`.
- **Never** call `.Result` or `.Wait()` on a `Task` inside an `async` method — deadlock or thread-pool starvation. Use `await`.
- A missing `await` on an async call silently discards the result. Only omit `await` when fire-and-forget is the **documented** intent.
- Async methods that perform I/O or network calls must accept a `CancellationToken` — otherwise they're uncooperative with graceful shutdown.

## Resource management

- `IDisposable` / `IAsyncDisposable` objects (especially `IDb`, streams, channels) must be wrapped in `using` — otherwise they leak.
- Outside code compiled with `EnableZkEvm=true`, do not add or retain a `try`/`finally` solely to guarantee `ArrayPool<T>.Shared.Return(array)` after an unexpected exception that aborts the operation. The abandoned array remains GC-reclaimable, so preserving pool reuse on that path generally does not justify an exception region. A shared-array return may remain in a `finally` already required for other cleanup.
- Return `ArrayPool<T>.Shared` rentals on the normal path and before an expected failure is handled, retried, or rethrown to a recovery boundary. If the code deliberately throws while it still owns the array, return it immediately before throwing. Never return an array that may already have been returned or handed off — a double return silently corrupts the pool.
- Do not generalize this exception to `SafeArrayPool<T>.Shared`, `ArrayPoolDisposableReturn`, `ArrayPoolList<T>`, custom pools, or other `Return`, `Release`, or `Dispose` contracts. The `using`-based helpers are permitted ownership conveniences; the other contracts may track ownership, use reference counting, or hold non-GC resources.
- `ReadBytes(n)` allocates a new ref-counted `IByteBuffer`. The method that allocates the buffer owns it; if ownership transfers (e.g. passing to a handler or message), the receiver becomes responsible for calling `Release()` / `SafeRelease()`. Forgetting to release, or releasing after ownership has transferred, are the two most common leak/corruption sources in the networking layer.
- Never swallow exceptions in an empty `catch` block — at minimum log the exception. Silent failures are the hardest to diagnose on a running node.

## Thread safety

- Shared mutable state (caches, peer tables, chain state) modified from multiple threads must use proper synchronisation — unsynchronised access causes data races and subtle corruption.

## Safety

- `unsafe` blocks must have a comment justifying the safety invariant — reviewers cannot verify correctness without it.
- Validate data from untrusted sources (P2P peers, RPC callers) before use — a `NullReferenceException` on external input is a crash vector.
