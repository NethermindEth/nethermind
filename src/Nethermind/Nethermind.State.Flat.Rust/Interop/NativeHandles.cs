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

internal sealed class ReaderHandle(RustFlatStore store, nint handle) : StoreChildHandle(store, handle)
{
    protected override FdbStatus Release(nint handle) => NativeMethods.fdb_reader_release(handle);
}

internal sealed class IteratorHandle(RustFlatStore store, nint handle) : StoreChildHandle(store, handle)
{
    protected override FdbStatus Release(nint handle) => NativeMethods.fdb_iter_release(handle);
}

internal sealed class BatchHandle(RustFlatStore store, nint handle) : StoreChildHandle(store, handle)
{
    protected override FdbStatus Release(nint handle) => NativeMethods.fdb_batch_release(handle);
}

/// <summary>A native snapshot manager. Releasing it shuts the manager down, then frees the host its callbacks reach.</summary>
/// <remarks>A manager that failed to be destroyed may still call its host, which is then kept alive.</remarks>
internal sealed class ManagerHandle(RustFlatStore store, nint handle, GCHandle host) : StoreChildHandle(store, handle)
{
    protected override FdbStatus Release(nint handle)
    {
        FdbStatus status = NativeMethods.fdb_destroy(handle);
        if (status == FdbStatus.Ok) host.Free();
        return status;
    }
}
