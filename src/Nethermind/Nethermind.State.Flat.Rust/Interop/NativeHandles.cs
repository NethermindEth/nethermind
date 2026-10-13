// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.InteropServices;

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
