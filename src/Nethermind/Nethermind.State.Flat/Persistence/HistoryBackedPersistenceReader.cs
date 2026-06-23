// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Trie;

namespace Nethermind.State.Flat.Persistence;

/// <summary>
/// An <see cref="IPersistence.IPersistenceReader"/> pinned to a single historical block, serving account and storage
/// reads from the finalized history index via <see cref="HistoryReader"/>. Backs a read-only world-state scope for
/// blocks below the finalization barrier, whose tip snapshots have been pruned. Only account/slot reads are
/// load-bearing; trie-node, raw and iteration members are unused on the read/eth_call path (mirrors
/// <see cref="NoopPersistenceReader"/>).
/// </summary>
public sealed class HistoryBackedPersistenceReader(HistoryReader historyReader, StateId block) : IPersistence.IPersistenceReader
{
    public StateId CurrentState => block;

    public Account? GetAccount(Address address) =>
        historyReader.TryGetAccount(block.BlockNumber, address, out AccountStruct account)
            ? new Account(account.Nonce, account.Balance, account.StorageRoot.ToCommitment(), account.CodeHash.ToCommitment())
            : null;

    public bool TryGetSlot(Address address, in UInt256 slot, ref SlotValue outValue)
    {
        if (!historyReader.TryGetStorage(block.BlockNumber, address, slot, out SlotValue value)) return false;
        outValue = value;
        return true;
    }

    public void Dispose() { }

    public byte[]? TryLoadStateRlp(in TreePath path, ReadFlags flags) => null;

    public byte[]? TryLoadStorageRlp(Hash256 address, in TreePath path, ReadFlags flags) => null;

    public byte[]? GetAccountRaw(in ValueHash256 addrHash) => null;

    public bool TryGetStorageRaw(in ValueHash256 addrHash, in ValueHash256 slotHash, ref SlotValue value) => false;

    public IPersistence.IFlatIterator CreateAccountIterator(in ValueHash256 startKey, in ValueHash256 endKey) => new EmptyIterator();

    public IPersistence.IFlatIterator CreateStorageIterator(in ValueHash256 accountKey, in ValueHash256 startSlotKey, in ValueHash256 endSlotKey) => new EmptyIterator();

    public bool IsPreimageMode => false;

    private struct EmptyIterator : IPersistence.IFlatIterator
    {
        public bool MoveNext() => false;
        public ValueHash256 CurrentKey => default;
        public ReadOnlySpan<byte> CurrentValue => default;
        public void Dispose() { }
    }
}
