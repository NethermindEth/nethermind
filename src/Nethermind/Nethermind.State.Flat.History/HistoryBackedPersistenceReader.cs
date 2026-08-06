// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;

namespace Nethermind.State.Flat.History;

/// <summary>
/// An <see cref="IPersistence.IPersistenceReader"/> pinned to one historical block, serving account and storage
/// reads from the history index. Flat history keeps no trie nodes, raw-import data, or iteration order, so those
/// members throw — a historical trie traversal must fail loudly as unsupported, not silently produce a wrong proof
/// or an empty state walk.
/// </summary>
/// <remarks>
/// <see cref="HistoricalFlatDbManager"/>'s own availability check, made before this reader is even constructed, is
/// only a routing decision — it is not atomic with this reader's scope registration, so a floor-advance's
/// publish-then-drain-then-delete could complete entirely in the gap between that check and
/// <see cref="HistoryScopeGate.EnterScope"/>. The constructor re-validates availability immediately after entering
/// the scope, closing that window: any floor advance from this point on is guaranteed to see this scope in its
/// drain (or this construction fails closed instead of serving a block whose rows may already be gone).
/// </remarks>
internal sealed class HistoryBackedPersistenceReader : IPersistence.IPersistenceReader
{
    private readonly HistoryReader _historyReader;
    private readonly StateId _block;
    private readonly HistoryScopeGate _scopeGate;
    private readonly StorageClearsScopeCache _clearsCache = new();
    private readonly int _scopeEpoch;

    public HistoryBackedPersistenceReader(HistoryReader historyReader, StateId block, HistoryScopeGate scopeGate)
    {
        _historyReader = historyReader;
        _block = block;
        _scopeGate = scopeGate;
        _scopeEpoch = scopeGate.EnterScope();

        if (!historyReader.IsAvailable(block))
        {
            scopeGate.ExitScope(_scopeEpoch);
            throw StateUnavailable(new StateUnavailableException(
                $"Historical state for block {block.BlockNumber} is unavailable" +
                (historyReader.IsPrunedBelowFloor(block.BlockNumber) ? " (pruned below the flat history retention floor)." : ".")));
        }
    }

    public StateId CurrentState => _block;

    public Account? GetAccount(Address address)
    {
        try
        {
            return _historyReader.TryGetAccount(_block.BlockNumber, address, out AccountStruct account)
                ? new Account(account.Nonce, account.Balance, account.StorageRoot.ToCommitment(), account.CodeHash.ToCommitment())
                : null;
        }
        catch (StateUnavailableException e)
        {
            throw StateUnavailable(e);
        }
    }

    public bool TryGetSlot(Address address, in UInt256 slot, ref SlotValue outValue)
    {
        try
        {
            if (!_historyReader.TryGetStorage(_block.BlockNumber, address, slot, out SlotValue value, _clearsCache)) return false;
            outValue = value;
            return true;
        }
        catch (StateUnavailableException e)
        {
            throw StateUnavailable(e);
        }
    }

    /// <summary>
    /// Translates "state unavailable" into <see cref="MissingTrieNodeException"/> — the hash-based reader's
    /// contract, which JSON-RPC maps to resource-not-found instead of an internal error.
    /// </summary>
    private MissingTrieNodeException StateUnavailable(StateUnavailableException inner) =>
        new($"Historical state for block {_block.BlockNumber} is unavailable", null, TreePath.Empty, _block.StateRoot.ToCommitment(), inner);

    public void Dispose() => _scopeGate.ExitScope(_scopeEpoch);

    public bool IsPreimageMode => false;

    public byte[]? TryLoadStateRlp(in TreePath path, ReadFlags flags) => throw Unsupported();

    public byte[]? TryLoadStorageRlp(Hash256 address, in TreePath path, ReadFlags flags) => throw Unsupported();

    public byte[]? GetAccountRaw(in ValueHash256 addrHash) => throw Unsupported();

    public bool TryGetStorageRaw(in ValueHash256 addrHash, in ValueHash256 slotHash, ref SlotValue value) => throw Unsupported();

    public IPersistence.IFlatIterator CreateAccountIterator(in ValueHash256 startKey, in ValueHash256 endKey) => throw Unsupported();

    public IPersistence.IFlatIterator CreateStorageIterator(in ValueHash256 accountKey, in ValueHash256 startSlotKey, in ValueHash256 endSlotKey) => throw Unsupported();

    private static NotSupportedException Unsupported() =>
        new($"{nameof(HistoryBackedPersistenceReader)} serves account/storage reads only; trie traversal, raw-import and iteration are unavailable for historical blocks below the finalization barrier.");
}
