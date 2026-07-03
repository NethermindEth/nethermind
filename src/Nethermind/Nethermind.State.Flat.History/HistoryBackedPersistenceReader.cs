// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;

namespace Nethermind.State.Flat.History;

/// <summary>
/// An <see cref="IPersistence.IPersistenceReader"/> pinned to a single historical block, serving account and storage
/// reads from the finalized history index via <see cref="HistoryReader"/>. Backs a read-only world-state scope for
/// blocks below the finalization barrier, whose tip snapshots have been pruned. Flat history retains no trie nodes,
/// raw-import data, or iteration order, so those members throw rather than return misleading empty/null results: a
/// historical trie traversal (e.g. <c>eth_getProof</c>, verifyTrie) must fail loudly as unsupported, not silently
/// produce a wrong proof or an empty state walk. Only the account/storage read members are serviceable.
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

    public bool IsPreimageMode => false;

    // Not serviceable from flat history (no trie nodes, no raw-import data, no iteration). Throw so a historical
    // trie traversal / scan fails loudly as unsupported instead of silently producing a wrong or empty result.
    public byte[]? TryLoadStateRlp(in TreePath path, ReadFlags flags) => throw Unsupported();

    public byte[]? TryLoadStorageRlp(Hash256 address, in TreePath path, ReadFlags flags) => throw Unsupported();

    public byte[]? GetAccountRaw(in ValueHash256 addrHash) => throw Unsupported();

    public bool TryGetStorageRaw(in ValueHash256 addrHash, in ValueHash256 slotHash, ref SlotValue value) => throw Unsupported();

    public IPersistence.IFlatIterator CreateAccountIterator(in ValueHash256 startKey, in ValueHash256 endKey) => throw Unsupported();

    public IPersistence.IFlatIterator CreateStorageIterator(in ValueHash256 accountKey, in ValueHash256 startSlotKey, in ValueHash256 endSlotKey) => throw Unsupported();

    private static NotSupportedException Unsupported() =>
        new($"{nameof(HistoryBackedPersistenceReader)} serves account/storage reads only; trie traversal, raw-import and iteration are unavailable for historical blocks below the finalization barrier.");
}
