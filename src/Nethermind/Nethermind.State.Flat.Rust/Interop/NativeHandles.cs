// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.InteropServices;
using Microsoft.Extensions.ObjectPool;

namespace Nethermind.State.Flat.Rust.Interop;

/// <summary>
/// A reader, iterator or batch of a <see cref="RustFlatStore"/>. It holds a reference on the store, so the store is
/// destroyed only once every handle created from it is released, as the native side requires.
/// </summary>
internal abstract class StoreChildHandle : SafeHandle
{
    private readonly RustFlatStore _store;

    /// <param name="store">The store; its reference, taken before the native handle was created, passes to this handle.</param>
    /// <param name="handle">The native handle.</param>
    protected StoreChildHandle(RustFlatStore store, nint handle) : base(0, ownsHandle: true)
    {
        _store = store;
        SetHandle(handle);
    }

    /// <summary>The store the handle was created from.</summary>
    public RustFlatStore Store => _store;

    public override bool IsInvalid => handle == 0;

    protected sealed override bool ReleaseHandle()
    {
        bool released = Release(handle) == FdbStatus.Ok;
        _store.DangerousRelease();
        return released;
    }

    protected abstract FdbStatus Release(nint handle);
}

internal class ReaderHandle(RustFlatStore store, nint handle) : StoreChildHandle(store, handle)
{
    protected override FdbStatus Release(nint handle) => NativeMethods.fdb_reader_release(handle);
}

/// <summary>A bundle gathered from a native manager, which it keeps alive, as it reads through the manager's snapshots.</summary>
/// <param name="store">The store; its reference, taken before the native handle was created, passes to this handle.</param>
/// <param name="manager">The manager; its reference, taken with the store's, passes to this handle too.</param>
/// <param name="handle">The native bundle.</param>
internal sealed class BundleHandle(RustFlatStore store, ManagerHandle manager, nint handle) : ReaderHandle(store, handle)
{
    protected override FdbStatus Release(nint handle)
    {
        FdbStatus status = base.Release(handle);
        manager.DangerousRelease();
        return status;
    }
}

/// <summary>
/// A native read-only bundle or scope gathered from a manager, which it keeps alive, as it reads through the manager's
/// snapshots. A scope's handle also serves as its base view: the read-only bundle it reads below its own snapshots.
/// </summary>
/// <remarks>
/// Calls pass the raw pointer of a <see cref="Call"/> lease rather than the handle, so that a scope can lease itself
/// without the reference count every <see cref="SafeHandle"/> call writes.
/// </remarks>
/// <param name="store">The store; its reference, taken before the native handle was created, passes to this handle.</param>
/// <param name="manager">The manager; its reference, taken with the store's, passes to this handle too.</param>
/// <param name="handle">The native bundle or scope.</param>
internal abstract class BundleBaseHandle(RustFlatStore store, ManagerHandle manager, nint handle) : StoreChildHandle(store, handle)
{
    /// <summary>The manager the bundle was gathered from.</summary>
    public ManagerHandle Manager => manager;

    /// <summary>Leases the native bundle for one call; dispose the lease when the call returns.</summary>
    /// <exception cref="ObjectDisposedException">The handle is disposed.</exception>
    public abstract Call Enter();

    protected sealed override FdbStatus Release(nint handle)
    {
        FdbStatus status = ReleaseBundle(handle);
        manager.DangerousRelease();
        return status;
    }

    protected abstract FdbStatus ReleaseBundle(nint handle);

    /// <summary>
    /// A lease of the native bundle for one call: <see cref="Bundle"/> stays valid until it is disposed. It holds either
    /// a count on a scope's call stripe or a reference on the handle.
    /// </summary>
    public readonly ref struct Call
    {
        private readonly ref int _stripeCount;
        private readonly SafeHandle? _referencedHandle;

        /// <param name="bundle">The native bundle.</param>
        /// <param name="stripeCount">The stripe count the lease incremented.</param>
        public Call(nint bundle, ref int stripeCount)
        {
            Bundle = bundle;
            _stripeCount = ref stripeCount;
        }

        /// <param name="referencedHandle">The handle the lease added a reference on.</param>
        public Call(SafeHandle referencedHandle)
        {
            Bundle = referencedHandle.DangerousGetHandle();
            _referencedHandle = referencedHandle;
        }

        public nint Bundle { get; }

        public void Dispose()
        {
            if (_referencedHandle is null) Interlocked.Decrement(ref _stripeCount);
            else _referencedHandle.DangerousRelease();
        }
    }
}

/// <summary>A native read-only bundle, released with <c>fdb_rob_release</c>, whose calls hold a reference on the handle.</summary>
internal sealed class RobHandle(RustFlatStore store, ManagerHandle manager, nint handle) : BundleBaseHandle(store, manager, handle)
{
    public override Call Enter()
    {
        bool added = false;
        DangerousAddRef(ref added);
        return new Call(this);
    }

    protected override FdbStatus ReleaseBundle(nint handle) => NativeMethods.fdb_rob_release(handle);
}

/// <summary>A native scope, released with <c>fdb_scope_release</c>, whose calls hold a striped lease rather than the handle.</summary>
/// <remarks>
/// Many threads call the main block's scope at once (storage workers, block-access-list readers, prewarmers); the
/// reference count every <see cref="SafeHandle"/> call takes would be one cache line they all write. A call instead
/// leases the scope with <see cref="Enter"/>, counting itself on its thread's stripe, one of <see cref="StripeCount"/>
/// on cache lines of their own, and passes the raw pointer. Disposing waits for every stripe to drain before the handle
/// is released, so no call outlives the native scope; a call entered after that throws
/// <see cref="ObjectDisposedException"/>. The stripes are pooled, as a scope is opened for every read-only bundle.
/// </remarks>
internal sealed class ScopeHandle(RustFlatStore store, ManagerHandle manager, nint handle) : BundleBaseHandle(store, manager, handle)
{
    private const int StripeCount = 16;
    private const int MaxRetainedStripes = 256;

    private static readonly ObjectPool<CallStripe[]> StripePool =
        new DefaultObjectPool<CallStripe[]>(new StripesPolicy(), MaxRetainedStripes);

    private readonly CallStripe[] _stripes = StripePool.Get();
    private volatile bool _closing;
    private int _disposing;

    public override Call Enter()
    {
        ref int count = ref _stripes[Environment.CurrentManagedThreadId & (StripeCount - 1)].Count;
        Interlocked.Increment(ref count);
        if (_closing)
        {
            Interlocked.Decrement(ref count);
            throw new ObjectDisposedException(nameof(ScopeHandle));
        }

        return new Call(handle, ref count);
    }

    protected override void Dispose(bool disposing)
    {
        // Only the first dispose drains: by the time of a later one the stripes may count another scope's calls.
        if (Interlocked.Exchange(ref _disposing, 1) != 0) return;

        // A full fence on each side: a call either sees the flag, or its count is seen here.
        _closing = true;
        Interlocked.MemoryBarrier();
        SpinWait spin = default;
        foreach (ref CallStripe stripe in _stripes.AsSpan())
        {
            while (Volatile.Read(ref stripe.Count) != 0) spin.SpinOnce();
        }

        base.Dispose(disposing);
        // A call entered later counts itself on a stripe another scope may own by then, but leaves it as it found it.
        StripePool.Return(_stripes);
    }

    protected override FdbStatus ReleaseBundle(nint handle) => NativeMethods.fdb_scope_release(handle);

    /// <remarks>
    /// The count sits at the end of its 64 bytes: an array's elements start 16 bytes into the object, 8 after its length,
    /// which every call reads to check its index, so the first stripe's count would otherwise share the length's line.
    /// </remarks>
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct CallStripe
    {
        [FieldOffset(56)] public int Count;
    }

    private sealed class StripesPolicy : PooledObjectPolicy<CallStripe[]>
    {
        public override CallStripe[] Create() => new CallStripe[StripeCount];

        public override bool Return(CallStripe[] obj) => true;
    }
}

/// <summary>A snapshot a native scope committed, which needs neither the store nor the manager.</summary>
internal sealed class SnapshotHandle : SafeHandle
{
    public SnapshotHandle(nint handle) : base(0, ownsHandle: true) => SetHandle(handle);

    public override bool IsInvalid => handle == 0;

    protected override bool ReleaseHandle() => NativeMethods.fdb_snapshot_release(handle) == FdbStatus.Ok;
}

internal sealed class IteratorHandle(RustFlatStore store, nint handle) : StoreChildHandle(store, handle)
{
    protected override FdbStatus Release(nint handle) => NativeMethods.fdb_iter_release(handle);
}

internal sealed class BatchHandle(RustFlatStore store, nint handle) : StoreChildHandle(store, handle)
{
    protected override FdbStatus Release(nint handle) => NativeMethods.fdb_batch_release(handle);
}

/// <summary>
/// A native snapshot manager. Releasing it, once the bundles gathered from it are released too, publishes its metrics
/// a last time, shuts the manager down, then frees the host its callbacks reach.
/// </summary>
/// <remarks>A manager that failed to be destroyed may still call its host, which is then kept alive.</remarks>
internal sealed class ManagerHandle(RustFlatStore store, nint handle, GCHandle host) : StoreChildHandle(store, handle)
{
    private readonly RustMetricsBridge _metrics = new();

    /// <summary>Publishes what changed in the manager's metrics since the last call; does nothing once released.</summary>
    public void PublishMetrics()
    {
        bool added = false;
        try
        {
            DangerousAddRef(ref added);
            _metrics.Publish(DangerousGetHandle());
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            if (added) DangerousRelease();
        }
    }

    protected override FdbStatus Release(nint handle)
    {
        try
        {
            // What the bundles released last changed, the slot filter memory they gave back say, reaches the metrics.
            _metrics.Publish(handle);
        }
        catch (Exception)
        {
            // A release must not throw, and a failed last publish must not keep the manager, the store and the
            // database alive.
        }

        FdbStatus status = NativeMethods.fdb_destroy(handle);
        if (status == FdbStatus.Ok) host.Free();
        return status;
    }
}
