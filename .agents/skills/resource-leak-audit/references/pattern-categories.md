# Pattern Categories to Search

Search ALL of these (Full Audit Mode) or the relevant subset (PR Mode).

## Tier 1 — High-Frequency Accumulating Leaks (per-peer, per-block, per-request)

- [ ] **CancellationTokenSource** — every `new CancellationTokenSource(` and `CreateLinkedTokenSource(`. Is `.Dispose()` called? Not just `.Cancel()` — cancelling does NOT dispose. Linked CTS especially leak. Also search `.Cancel()` on CTS fields and verify `.Dispose()` follows.
- [ ] **Byte array / buffer pool leaks** — `ArrayPool<T>.Shared.Rent(` without `Return(`. Return must be in `try/finally` — any exception between Rent and Return leaks the buffer.
- [ ] **Ref-counted network buffers** — Buffer allocation (`ReadBytes(`, `Allocate(`, `Buffer(`) without `Release()` / `SafeRelease()` / `Complete()`. Check error paths. Discover buffer types by searching `Release()` and `SafeRelease()` definitions.
- [ ] **TaskCompletionSource never completed** — Field declarations. Uncompleted TCS holds continuation chain alive forever. Verify all paths complete or cancel. Check disconnect/timeout/Dispose/shutdown. Check thread-safety on reassignment.
- [ ] **Event handler subscriptions preventing GC** — `+=` without matching `-=`. Search `+=` in constructors, check for `-=` in Dispose. Also search empty Dispose() bodies.

## Tier 1.5 — Cross-Cutting Lifecycle and Coordination Leaks

- [ ] **Async coordination with broken lifecycle** — TCS fields for handshakes between async loops. Is TCS signaled on ALL exit paths? Does consumer use `WaitAsync(token)` or timeout? Is old TCS completed before reassignment?
- [ ] **Shutdown ordering races in Task.WhenAll** — Multiple long-running tasks sharing CancellationToken. Can task B call methods on task A's objects after A has exited?
- [ ] **CancellationToken accepted but ignored** — Methods accepting `CancellationToken` but never using it in the body.
- [ ] **Double-dispose on same logical path** — Method disposes resource, then caller's `finally` disposes it again. Deterministic, not a race.
- [ ] **Ownership ambiguity across method boundaries** — Resource created by A, passed to B, neither disposes. Focus on factory methods flowing through multiple layers.
- [ ] **`finally` blocks that signal but don't clean up** — Async loops with `finally` that log but don't complete channels, signal TCS, or dispose resources.
- [ ] **Override methods discarding disposable parameters** — Derived class ignoring disposable parameter from parent.
- [ ] **Interfaces severing disposal chains** — Interface doesn't extend IDisposable, callers can't dispose concrete implementation. Report the interface as root cause.

## Tier 2 — Error-Path and Shutdown Leaks

- [ ] **HTTP disposables** — HttpClient, HttpResponseMessage, HttpRequestMessage, StringContent, ByteArrayContent, FormUrlEncodedContent.
- [ ] **All Stream subclasses** — MemoryStream, FileStream, StreamReader/Writer, BinaryReader/Writer, compression streams, CryptoStream, NetworkStream.
- [ ] **Database handles** — DB abstractions with `Db`, `Batch`, `Snapshot`, `Reader`, `Writer`. Focus on `StartWriteBatch`/`CreateSnapshot`.
- [ ] **Disposable return values** — Methods returning IDisposable. Check ALL callers. Focus on `Create*`, `Open*`, `Build*`, `Get*Stream`, `Start*Batch`.
- [ ] **Constructor exception leaks** — Constructor creates A, then B throws, A is leaked. Also `new Foo(CreateA(), CreateB())`.
- [ ] **Timer types** — System.Timers.Timer, System.Threading.Timer, PeriodicTimer.
- [ ] **Synchronization primitives** — SemaphoreSlim, ManualResetEvent(Slim), AutoResetEvent, ReaderWriterLockSlim, Mutex, EventWaitHandle.
- [ ] **Channel\<T\>** — Is Writer.Complete() called? Are readers drained?
- [ ] **Empty or incomplete Dispose** — Dispose bodies empty, log-only, or missing disposable fields.
- [ ] **IEnumerator\<T\>** — explicit GetEnumerator() without Dispose. `foreach` auto-disposes, manual doesn't. Also IAsyncEnumerator.
- [ ] **PipeReader / PipeWriter** — Check Complete() is called.
- [ ] **JsonDocument** — JsonDocument.Parse() must be disposed.
- [ ] **Process** — new Process(), Process.Start().
- [ ] **WebSocket / TcpClient / Socket** — network-level disposables.
- [ ] **Scope/transaction objects** — IServiceScope, ServiceProvider, TransactionScope, DbTransaction, ILifetimeScope (Autofac).
- [ ] **X509Certificate / X509Certificate2** — crypto disposables.
- [ ] **RSA / ECDsa / other crypto handles** — RSA.Create(), ECDsa.Create().

## Tier 3 — Structural and Long-Lived Leaks

- [ ] **Unbounded collection growth** — Dictionary, ConcurrentDictionary, List, HashSet without eviction. Especially keyed by peer ID, block hash, or tx hash.
- [ ] **Unbounded ConcurrentQueue fields** — No completion mechanism. If consumer dies, grows unbounded.
- [ ] **Dispose race conditions** — `_disposed` as plain `bool` without Interlocked. Check-then-act races allow double-dispose or use-after-dispose.
- [ ] **Static fields accumulating** — static readonly Dictionary, List, ConcurrentDictionary, static event.
- [ ] **Closure captures extending lifetimes** — Lambdas in Task.Run, ContinueWith, events, timers capturing `this` or large objects.

## Tier 4 — Edge-Case, Diagnostic, and Native Patterns

- [ ] **Native handles without Dispose** — Objects like RocksDB ReadOptions. Per-operation creation accumulates on finalizer queue.
- [ ] **LOH allocations** — `new byte[N]` where N >= 85000. Variable sizes from untrusted input.
- [ ] **Pinned memory** — GCHandle.Alloc with Pinned without Free(). Focus on GCHandle, not `fixed`.
- [ ] **Unmanaged memory** — Marshal.AllocHGlobal, AllocCoTaskMem, NativeMemory.Alloc without Free.
- [ ] **ThreadLocal / AsyncLocal** — Holding disposable/large objects. ThreadLocal is IDisposable.
- [ ] **Abandoned Tasks** — Fire-and-forget Task.Run, `_ = SomeAsync()` capturing references.
- [ ] **Finalizer queue pressure** — Classes with ~Finalizer() created at high frequency undisposed.
- [ ] **WeakReference misuse** — WeakReference objects accumulating. ConditionalWeakTable with long-lived keys.
- [ ] **String interning** — string.Intern() or static dictionaries of strings.
- [ ] **async void** — exceptions crash process, skip finally, leak resources.
- [ ] **Lazy\<T\> with disposable** — Lazy caches forever. If T is IDisposable, never cleaned up.

## Additional Search Strategies

### Pattern-Based
- Prior leak-fix PRs via `git log --grep="dispose" --grep="leak" --all-match`
- Cancel() without Dispose() on CTS fields
- Empty/incomplete Dispose() bodies
- Compound expressions creating multiple disposables
- Overridden methods discarding disposable parameters
- `_disposed` flags without Interlocked
- Factory methods returning disposables (`Create*`, `Open*`, `Build*`)
- `Task.Run` / `Task.Factory.StartNew` closures
- `GC.SuppressFinalize` without Dispose
- WeakReference / ConditionalWeakTable accumulation

### Safe-Wrapper Bypass
- Raw pool rental without codebase's disposable wrapper
- Manual `.Dispose()` on locals instead of `using var`
- `.SetResult()` instead of `.TrySetResult()` on TCS
- `bool _disposed` when codebase convention is `Interlocked.Exchange`

### Interaction-Tracing
- `Task.WhenAll` launching async loops — cross-task calls during shutdown
- `CancellationToken` parameters never referenced in method body
- Dispose chains across ownership boundaries (A creates, passes to C, who disposes?)
- `finally` blocks in async loops — do they complete channels/TCS or just log?
