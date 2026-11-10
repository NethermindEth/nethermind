// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Utils;
using Nethermind.State.Pbt.NodeBuffer;
using Nethermind.State.Pbt.Snapshot;
using IResettable = Nethermind.Core.Resettables.IResettable;

namespace Nethermind.State.Pbt;

/// <summary>
/// Contains the large per-block state used by <see cref="PbtSnapshotBundle"/> but not committed into <see cref="IPbtDbManager"/>
/// as part of a <see cref="PbtSnapshot"/>. Pooling this is largely for performance reason.
/// </summary>
public sealed class PbtTransientResource(PbtTransientResource.Size size) : IDisposable, IResettable
{
    public record Size(int NodeGroupsCacheSize);

    // Invariant: the pool return runs exactly once, at refcount zero, so an in-flight prefetch
    // lookup can never overlap Reset/re-rent of this resource.
    private long _leases = RefCountingLease.Single;
    private IPbtResourcePool? _returnPool;
    private PbtResourcePool.Usage _returnUsage;
    private int _nodeGroupsCacheSize = size.NodeGroupsCacheSize;

    public PbtWarmedGroupBuffer WarmedGroupBuffer { get; } = new(size.NodeGroupsCacheSize);

    public void OnRented(IPbtResourcePool pool, PbtResourcePool.Usage usage)
    {
        _returnPool = pool;
        _returnUsage = usage;
        Volatile.Write(ref _leases, RefCountingLease.Single);
    }

    public bool TryAcquireLease() => RefCountingLease.TryAcquire(ref _leases);

    /// <summary>
    /// Releases one lease; the final release returns the resource to the pool it was checked out from.
    /// </summary>
    /// <remarks>
    /// Deliberately not <see cref="Dispose"/> (the <c>RefCountingDisposable</c> convention): the pool's
    /// <c>IDisposable</c> contract reserves <see cref="Dispose"/> for destroying an over-capacity resource
    /// (freeing the <see cref="PbtWarmedGroupBuffer"/>), so it cannot double as the return-to-pool path.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The final release found no return pool registered,
    /// i.e. the resource was not checked out through <see cref="PbtResourcePool.GetCachedResource"/>.</exception>
    public void ReleaseLease()
    {
        if (RefCountingLease.ReleaseOnce(ref _leases))
        {
            if (_returnPool is null)
            {
                throw new InvalidOperationException($"{nameof(PbtTransientResource)} final lease released without a registered return pool");
            }

            _returnPool.ReturnCachedResource(_returnUsage, this);
        }
    }

    public Size GetSize() => new(_nodeGroupsCacheSize);

    public void Reset()
    {
        _nodeGroupsCacheSize = Math.Max(_nodeGroupsCacheSize, WarmedGroupBuffer.Count);
        WarmedGroupBuffer.Reset();
    }

    public void Dispose() => WarmedGroupBuffer.Dispose();
}
