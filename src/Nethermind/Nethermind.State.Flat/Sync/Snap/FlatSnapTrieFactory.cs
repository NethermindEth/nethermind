// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Snap;
using Nethermind.Synchronization.SnapSync;
using Nethermind.Trie;

namespace Nethermind.State.Flat.Sync.Snap;

/// <summary>
/// ISnapTrieFactory implementation for flat state storage.
/// Uses IPersistence to create reader/writeBatch per tree for proper resource management.
/// EnsureInitialize/FinalizeSync are driven by the snap-sync runner at start/end of the run,
/// so they don't need internal locking — they're never called concurrently with CreateXxxTree.
/// </summary>
public class FlatSnapTrieFactory(IPersistence persistence, ISyncConfig syncConfig, ILogManager logManager) : ISnapTrieFactory
{
    private readonly ILogger _logger = logManager.GetClassLogger<FlatSnapTrieFactory>();

    public void EnsureInitialize()
    {
        if (_logger.IsInfo) _logger.Info("Clearing database");
        persistence.Clear();
    }

    public void FinalizeSync() => persistence.Flush();

    public ISnapTree<PathWithAccount> CreateStateTree()
    {
        IPersistence.IPersistenceReader reader = new LazyReader(persistence, ReaderFlags.Sync);
        IPersistence.IWriteBatch writeBatch = persistence.CreateWriteBatch(StateId.Sync, StateId.Sync, WriteFlags.DisableWAL);
        return new FlatSnapStateTree(reader, writeBatch, syncConfig.EnableSnapDoubleWriteCheck, logManager);
    }

    public ISnapTree<PathWithStorageSlot> CreateStorageTree(in ValueHash256 accountPath)
    {
        IPersistence.IPersistenceReader reader = new LazyReader(persistence, ReaderFlags.Sync);
        IPersistence.IWriteBatch writeBatch = persistence.CreateWriteBatch(StateId.Sync, StateId.Sync, WriteFlags.DisableWAL);
        return new FlatSnapStorageTree(reader, writeBatch, accountPath.ToCommitment(), syncConfig.EnableSnapDoubleWriteCheck, logManager);
    }

    /// <summary>
    /// Defers reader (and its underlying DB snapshot) creation until first use.
    /// </summary>
    /// <remarks>
    /// A snap tree is created once per account per storage response, but its reader is only ever
    /// touched on the proof/boundary-stitching and double-write-check paths — never in the dominant
    /// proofless path — so creating the snapshot eagerly is wasted work. Not thread-safe, matching
    /// the single-threaded use of each snap tree. Disposes the inner reader only if it was created.
    /// </remarks>
    private sealed class LazyReader(IPersistence persistence, ReaderFlags readerFlags) : IPersistence.IPersistenceReader
    {
        private IPersistence.IPersistenceReader? _inner;

        private IPersistence.IPersistenceReader Inner => _inner ??= persistence.CreateReader(readerFlags);

        public StateId CurrentState => Inner.CurrentState;
        public bool IsPreimageMode => Inner.IsPreimageMode;
        public Account? GetAccount(Address address) => Inner.GetAccount(address);
        public bool TryGetSlot(Address address, in UInt256 slot, ref SlotValue outValue) => Inner.TryGetSlot(address, slot, ref outValue);
        public byte[]? TryLoadStateRlp(in TreePath path, ReadFlags flags) => Inner.TryLoadStateRlp(path, flags);
        public byte[]? TryLoadStorageRlp(Hash256 address, in TreePath path, ReadFlags flags) => Inner.TryLoadStorageRlp(address, path, flags);
        public byte[]? GetAccountRaw(in ValueHash256 addrHash) => Inner.GetAccountRaw(addrHash);
        public bool TryGetStorageRaw(in ValueHash256 addrHash, in ValueHash256 slotHash, ref SlotValue value) => Inner.TryGetStorageRaw(addrHash, slotHash, ref value);
        public IPersistence.IFlatIterator CreateAccountIterator(in ValueHash256 startKey, in ValueHash256 endKey) => Inner.CreateAccountIterator(startKey, endKey);
        public IPersistence.IFlatIterator CreateStorageIterator(in ValueHash256 accountKey, in ValueHash256 startSlotKey, in ValueHash256 endSlotKey) => Inner.CreateStorageIterator(accountKey, startSlotKey, endSlotKey);
        public void Dispose() => _inner?.Dispose();
    }
}
