// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Int256;
using IResettable = Nethermind.Core.Resettables.IResettable;

namespace Nethermind.State.Pbt;

/// <summary>
/// The accounts and slots written by the block in flight, keyed as the caller wrote them, plus the
/// addresses it cleared.
/// </summary>
/// <remarks>
/// Everything this backend stores lives in stem leaf blobs, but those are only produced when the root
/// fold runs — so between a write and the next <see cref="ScopeProvider.PbtWorldStateScope.UpdateRootHash"/>
/// there is no blob to read the value back out of. This holds it in the meantime. It is a write-through
/// buffer, not a diff layer: sealing the block drops it, and every tier below reads through the blobs.
/// <para>
/// A cleared address is only recorded here, and only until the block is sealed, because the layers
/// below cannot represent the ordering it implies: the fold cannot enumerate an address's storage-zone
/// stems to clear them, so what the marker masks is whatever a stem's blob still holds, and a blob
/// cannot say whether it predates the clear. Within the block that is not a problem —
/// <see cref="PbtSnapshotBundle.SelfDestruct"/> drops the pending slots and the writes that follow the
/// clear land on top of it, which is the order <c>ProcessStorageChanges</c> issues them in. It also
/// costs little: <c>PersistentStorageProvider.ClearStorage</c> zeroes every slot the block has read, so
/// only slots the block never touched depend on the marker at all.
/// </para>
/// <para>
/// The slot map is concurrent because block processing populates it from parallel storage write
/// batches; a present slot entry means "written in this block" and its value may legitimately be zero.
/// Pooled per <see cref="PbtResourcePool.Usage"/>, so an instance backs exactly one bundle at a time.
/// </para>
/// </remarks>
public sealed class PbtPendingFlatWrites : IDisposable, IResettable
{
    public ConcurrentDictionary<AddressAsKey, Account?> Accounts { get; } = new();
    public ConcurrentDictionary<(AddressAsKey Address, UInt256 Slot), EvmWord> Slots { get; } = new();
    public ConcurrentDictionary<AddressAsKey, bool> SelfDestructs { get; } = new();

    /// <remarks>
    /// The lock-free clears are sound only where the block's storage batches have been joined: at the
    /// commit that seals the block, and at a pool-return boundary.
    /// </remarks>
    public void Reset()
    {
        Accounts.NoLockClear();
        Slots.NoLockClear();
        SelfDestructs.NoLockClear();
    }

    /// <remarks>No-op: the maps hold nothing unmanaged. Present only so the pool can discard an instance it has no room to hold.</remarks>
    public void Dispose()
    {
    }
}
