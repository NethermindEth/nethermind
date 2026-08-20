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
/// members throw rather than silently produce a wrong proof or an empty state walk.
/// </summary>
/// <remarks>
/// The routing check in <see cref="HistoricalFlatDbManager"/> is not atomic with scope registration, so the
/// constructor re-validates availability after <see cref="HistoryScopeGate.EnterScope"/>: any floor advance from
/// that point either waited on this scope or is observed by the re-validation, which fails closed. The slice
/// scope set is resolved after entering for the same reason.
/// </remarks>
internal sealed class HistoryBackedPersistenceReader : IPersistence.IPersistenceReader
{
    private readonly HistoryReader _historyReader;
    private readonly StateId _block;
    private readonly HistoryScopeGate _scopeGate;
    private readonly StorageClearsScopeCache _clearsCache = new();
    private readonly long _scopeToken;

    // Non-null only below the general retention floor, where per-address slices are all that remains: every read
    // checks the requested address against this in-memory set and fails closed for anything outside it.
    private readonly IReadOnlyList<ScopeFloor>? _sliceScopes;

    public HistoryBackedPersistenceReader(HistoryReader historyReader, StateId block, HistoryScopeGate scopeGate, bool restrictToSlices = false)
    {
        _historyReader = historyReader;
        _block = block;
        _scopeGate = scopeGate;
        _scopeToken = scopeGate.EnterScope();

        if (restrictToSlices) _sliceScopes = historyReader.GetSliceScopes();

        bool available = restrictToSlices ? historyReader.IsCoveredAndRootMatches(block) : historyReader.IsAvailable(block);
        if (!available)
        {
            scopeGate.ExitScope(_scopeToken);
            throw StateUnavailable(new StateUnavailableException(
                $"Historical state for block {block.BlockNumber} is unavailable" +
                (!restrictToSlices && historyReader.IsPrunedBelowFloor(block.BlockNumber) ? " (pruned below the flat history retention floor)." : ".")));
        }
    }

    public StateId CurrentState => _block;

    public Account? GetAccount(Address address)
    {
        if (_sliceScopes is not null) RequireRetainedBySlice(address);
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
        if (_sliceScopes is not null) RequireRetainedBySlice(address);
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

    /// <summary>Fails closed for an address outside every retained slice: below the general floor only sliced
    /// addresses (each with its own floor) still have rows. In-memory only, no DB read.</summary>
    private void RequireRetainedBySlice(Address address)
    {
        ReadOnlySpan<byte> key = address.ToAccountPath.Bytes[..HistoryKeyLayout.ScopeKeyLength];
        for (int i = 0; i < _sliceScopes!.Count; i++)
        {
            ScopeFloor scope = _sliceScopes[i];
            if (_block.BlockNumber >= scope.Floor && ((ReadOnlySpan<byte>)scope.Key).SequenceEqual(key)) return;
        }

        throw StateUnavailable(new StateUnavailableException(
            $"Historical state for block {_block.BlockNumber} is unavailable for {address} - it is below the general retention floor and not covered by any retained slice."));
    }

    /// <summary>
    /// Translates "state unavailable" into <see cref="MissingTrieNodeException"/> — the hash-based reader's
    /// contract, which JSON-RPC maps to resource-not-found instead of an internal error.
    /// </summary>
    private MissingTrieNodeException StateUnavailable(StateUnavailableException inner) =>
        new($"Historical state for block {_block.BlockNumber} is unavailable", null, TreePath.Empty, _block.StateRoot.ToCommitment(), inner);

    public void Dispose() => _scopeGate.ExitScope(_scopeToken);

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
