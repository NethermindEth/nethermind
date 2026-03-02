# C#/.NET Robustness

Patterns that cause silent failures, resource leaks, or deadlocks in production. These apply to all C# code in the repo.

## Async

- **Never** use `async void` — exceptions are silently swallowed. Use `async Task`.
- **Never** call `.Result` or `.Wait()` on a `Task` inside an `async` method — deadlock or thread-pool starvation. Use `await`.
- A missing `await` on an async call silently discards the result. Only omit `await` when fire-and-forget is the **documented** intent.
- Async methods that perform I/O or network calls must accept a `CancellationToken` — otherwise they're uncooperative with graceful shutdown.

## Resource management

- `IDisposable` / `IAsyncDisposable` objects (especially `IDb`, streams, channels) must be wrapped in `using` — otherwise they leak.
- Never swallow exceptions in an empty `catch` block — at minimum log the exception. Silent failures are the hardest to diagnose on a running node.

## Safety

- `unsafe` blocks must have a comment justifying the safety invariant — reviewers cannot verify correctness without it.
- Validate data from untrusted sources (P2P peers, RPC callers) before use — a `NullReferenceException` on external input is a crash vector.
