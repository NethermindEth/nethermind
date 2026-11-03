// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Paprika.Store;

namespace Nethermind.State.Flat.Persistence.PaprikaFlat;

/// <summary>The accounts and slots of <see cref="Db.FlatLayout.PaprikaFlat"/> as of the root <paramref name="batch"/> reads.</summary>
/// <remarks>
/// Thread safe, as Paprika readers are. A stored empty value reads as absent, as Paprika stores a delete as one. Prefetch is
/// not supported (<see cref="BasePersistence.IHashedFlatReader.SupportsPrefetch"/> stays false): a read of the mapped file
/// is synchronous, and nothing tells cheaply whether it would hit memory.
/// </remarks>
internal readonly struct PaprikaFlatReader(IVisitableReadOnlyBatch batch) : BasePersistence.IHashedFlatReader
{
    /// <inheritdoc/>
    public bool IsPreimageMode => false;

    /// <inheritdoc/>
    public int GetAccount(in ValueHash256 address, Span<byte> outBuffer)
    {
        if (!batch.TryGet(PaprikaFlatKeys.Account(address.Bytes), out ReadOnlySpan<byte> value)) return 0;
        value.CopyTo(outBuffer);
        return value.Length;
    }

    /// <inheritdoc/>
    public bool TryGetStorage(in ValueHash256 address, in ValueHash256 slot, ref UInt256 outValue)
    {
        ValueHash256 storageAccount = PaprikaFlatKeys.StorageAccount(address.Bytes);
        if (!batch.TryGet(PaprikaFlatKeys.Slot(storageAccount.Bytes, slot.Bytes), out ReadOnlySpan<byte> value) || value.IsEmpty) return false;
        outValue = new UInt256(PaprikaFlatRanges.DecodeSlot(value), isBigEndian: true);
        return true;
    }

    /// <inheritdoc/>
    public IPersistence.IFlatIterator CreateAccountIterator(in ValueHash256 startKey, in ValueHash256 endKey) =>
        PaprikaFlatRanges.CreateAccountIterator(batch, startKey, endKey);

    /// <inheritdoc/>
    public IPersistence.IFlatIterator CreateStorageIterator(in ValueHash256 accountKey, in ValueHash256 startSlotKey, in ValueHash256 endSlotKey) =>
        PaprikaFlatRanges.CreateStorageIterator(batch, accountKey, startSlotKey, endSlotKey);
}
