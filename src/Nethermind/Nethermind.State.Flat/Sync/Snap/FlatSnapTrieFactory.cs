// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Diagnostics;
using Autofac.Features.AttributeFilters;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat.Migration;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Snap;
using Nethermind.Synchronization.SnapSync;
using Nethermind.Trie;
using Nethermind.Trie.NodeBuffer;

namespace Nethermind.State.Flat.Sync.Snap;

/// <summary>
/// ISnapTrieFactory implementation for flat state storage.
/// Uses IPersistence to create reader/writeBatch per tree for proper resource management.
/// EnsureInitialize/FinalizeSync are driven by the snap-sync runner at start/end of the run,
/// so they don't need internal locking — they're never called concurrently with CreateXxxTree.
/// </summary>
public class FlatSnapTrieFactory(
    IPersistence persistence,
    IPersistenceManager persistenceManager,
    IColumnsDb<FlatDbColumns> flatDb,
    [KeyFilter(DbNames.Code)] IDb codeDb,
    ISyncConfig syncConfig,
    ILogManager logManager) : ISnapTrieFactory
{
    private const int MaxNodePathLength = 64;

    private readonly ILogger _logger = logManager.GetClassLogger<FlatSnapTrieFactory>();
    private readonly Lock _checkpointLock = new();
    private AccountRangeProgress[]? _savedPartitions;
    private Action? _invalidateStorageCheckpoint;

    private bool HasSavedCheckpoint => Volatile.Read(ref _savedPartitions) is not null;

    public void EnsureInitialize()
    {
        if (syncConfig.SnapResume && LoadCheckpoint() is { } checkpoint)
        {
            SetSavedPartitions(checkpoint);
            DiscardWritesAfter(checkpoint);
            persistenceManager.ResetPersistedStateId();
            return;
        }

        _savedPartitions = null;
        // Log before probing: on a database holding only tombstones from an earlier wipe, the probe itself can take a while.
        if (_logger.IsInfo) _logger.Info("Clearing database");
        if (_logger.IsWarn && HasAccounts())
            _logger.Warn("Flat snap sync cannot resume a previous run: discarding the state already in the database and restarting from scratch. Clearing a large database can take several minutes.");

        long startTime = Stopwatch.GetTimestamp();
        persistence.Clear();
        // The wipe drops the persisted state pointer; without this, HasStateForBlock keeps answering from the cached
        // pre-wipe pointer (genesis on a fresh node) for the whole sync, though no reader can serve that state anymore.
        persistenceManager.ResetPersistedStateId();
        if (_logger.IsInfo) _logger.Info($"Cleared database in {Stopwatch.GetElapsedTime(startTime).TotalSeconds:N1}s");
    }

    private bool HasAccounts() => HasAccounts(ValueKeccak.Zero, ValueKeccak.MaxValue);

    private bool HasAccounts(in ValueHash256 from, in ValueHash256 to)
    {
        using IPersistence.IPersistenceReader reader = persistence.CreateReader();
        using IPersistence.IFlatIterator accounts = reader.CreateAccountIterator(from, to);
        return accounts.MoveNext() || reader.GetAccountRaw(to) is not null;
    }

    public void FinalizeSync() => persistence.Flush();

    public bool SupportsCheckpoints => true;

    /// <inheritdoc/>
    /// <remarks>
    /// A checkpoint that cannot be read, was saved with a different number of account range partitions, or outlived a
    /// wipe of the data it describes, is not returned, so every caller, including those ahead of
    /// <see cref="EnsureInitialize"/>, treats it as gone.
    /// </remarks>
    public SnapCheckpoint? LoadCheckpoint()
    {
        byte[]? record = BasePersistence.ReadSnapCheckpoint(flatDb.GetColumnDb(FlatDbColumns.Metadata));
        if (record is null) return null;

        if (!BasePersistence.HasSnapCheckpointSentinel(flatDb))
        {
            if (_logger.IsWarn) _logger.Warn("Ignoring the snap sync checkpoint: the data it describes was wiped after it was saved, likely by an older Nethermind version.");
            return null;
        }

        SnapCheckpoint checkpoint;
        try
        {
            checkpoint = SnapCheckpoint.Decode(record);
        }
        catch (InvalidDataException e)
        {
            if (_logger.IsWarn) _logger.Warn($"Ignoring the snap sync checkpoint, which cannot be read: {e.Message}");
            return null;
        }

        if (checkpoint.Partitions.Length != syncConfig.SnapSyncAccountRangePartitionCount)
        {
            if (_logger.IsWarn) _logger.Warn($"Ignoring the snap sync checkpoint, saved with {checkpoint.Partitions.Length} account range partitions while {nameof(ISyncConfig.SnapSyncAccountRangePartitionCount)} is now {syncConfig.SnapSyncAccountRangePartitionCount}.");
            return null;
        }

        return checkpoint;
    }

    public void SaveCheckpoint(SnapCheckpoint? checkpoint)
    {
        lock (_checkpointLock)
        {
            SaveCheckpointCore(checkpoint);
            SetSavedPartitions(checkpoint);
        }
    }

    private void SetSavedPartitions(SnapCheckpoint? checkpoint) =>
        Volatile.Write(ref _savedPartitions, checkpoint?.Partitions.OrderBy(static p => p.AccountPathLimit).ToArray());

    private void SaveCheckpointCore(SnapCheckpoint? checkpoint)
    {
        if (checkpoint is not null)
        {
            // Snap writes skip the WAL, so only a flush makes the data the checkpoint covers durable. The persistence
            // flush drains the trie node log but only logs a column that fails to flush, so each column is flushed
            // again, throwing, before a checkpoint can claim the data. Codes are written with the WAL.
            persistence.Flush();
            foreach (FlatDbColumns column in Enum.GetValues<FlatDbColumns>()) flatDb.GetColumnDb(column).FlushOrThrow();
            codeDb.SyncWal();
        }

        BasePersistence.WriteSnapCheckpoint(flatDb, checkpoint?.Encode());
    }

    private void InvalidateCheckpointForRange(ValueHash256 from, ValueHash256 to, bool hasExactNode)
    {
        if (!HasSavedCheckpoint) return;
        if (!hasExactNode)
        {
            // Use the same whole account keys as the delete. An account at a partial boundary survives it.
            if (!FlatSnapStateTree.TryGetWholeAccountKeys(from, to, out ValueHash256 firstAccount, out ValueHash256 lastAccount)) return;
            (from, to) = (firstAccount, lastAccount);
        }

        lock (_checkpointLock)
        {
            if (_savedPartitions is not { } partitions) return;

            ValueHash256 start = ValueKeccak.Zero;
            foreach (AccountRangeProgress partition in partitions)
            {
                if (start > to) break;
                ValueHash256 first = from > start ? from : start;
                ValueHash256 last = to < partition.AccountPathLimit ? to : partition.AccountPathLimit;
                if (partition.MoreAccountsToRight && last >= partition.NextAccountPath)
                {
                    if (partition.NextAccountPath <= first)
                    {
                        start = partition.AccountPathLimit;
                        continue;
                    }
                    last = partition.NextAccountPath.DecrementPath();
                }

                // Empty proof ranges are common at an unchanged root. A covered trie subtree has at
                // least one account, so a range that has no accounts cannot remove saved progress.
                if (first <= last && (hasExactNode || HasAccounts(first, last)))
                {
                    SaveCheckpointCore(null);
                    Volatile.Write(ref _savedPartitions, null);
                    return;
                }
                start = partition.AccountPathLimit;
            }
        }
    }

    private void InvalidateSavedCheckpoint()
    {
        lock (_checkpointLock)
        {
            if (_savedPartitions is null) return;
            SaveCheckpointCore(null);
            SetSavedPartitions(null);
        }
    }

    /// <summary>Deletes everything snap sync wrote after <paramref name="checkpoint"/> was saved, keeping what it covers.</summary>
    /// <remarks>
    /// Writes made after the checkpoint may come from an older root, and part of them may be lost with the unflushed
    /// memtables. Downloading over them again would leave entries the newer root no longer has, so whatever the resumed
    /// run requests again is deleted first: the rest of each unfinished account partition with the storage of those
    /// accounts, and the queued storage ranges. What is left is what an uninterrupted run held at the checkpoint.
    /// <para>
    /// A trie node whose position lies within such a range is deleted with it. One whose position only overlaps the
    /// range lies on the path to one of its ends, and is deleted when the keys under it reach into the range: a range
    /// commit never persists a node whose keys reach past the committed range, so such a node was written after the
    /// checkpoint. A leaf or an extension on that path whose keys lie wholly outside the range is kept, as it may well
    /// predate the checkpoint, and trie reassembly can rebuild a missing branch from the flat entries but not a leaf.
    /// In a storage trie such a leaf or extension is deleted too: a range commit persists no storage node whose path
    /// reaches past the range, and reassembly rebuilds a missing slot leaf from the flat entries.
    /// </para>
    /// </remarks>
    private void DiscardWritesAfter(SnapCheckpoint checkpoint)
    {
        long startTime = Stopwatch.GetTimestamp();
        using (IPersistence.IPersistenceReader reader = persistence.CreateReader(ReaderFlags.Sync))
        using (IPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.Sync, StateId.Sync, WriteFlags.DisableWAL))
        {
            foreach (AccountRangeProgress partition in checkpoint.Partitions)
            {
                if (!partition.MoreAccountsToRight) continue;

                batch.DeleteStateTrieNodeRange(partition.NextAccountPath, partition.AccountPathLimit);
                DeleteNodesReachingInto(reader, batch, address: null, partition.NextAccountPath, partition.AccountPathLimit, wholePath: false);

                if (FlatSnapStateTree.TryGetWholeAccountKeys(partition.NextAccountPath, partition.AccountPathLimit, out ValueHash256 firstAccount, out ValueHash256 lastAccount))
                {
                    batch.DeleteAccountRange(firstAccount, lastAccount);
                    batch.SelfDestructRange(firstAccount, lastAccount, null);
                }
            }

            foreach ((ValueHash256 accountPath, ValueHash256 from, ValueHash256 to) in checkpoint.StorageRangesToRefetch())
            {
                batch.DeleteStorageRange(accountPath, from, to);
                batch.DeleteStorageTrieNodeRange(accountPath, from, to);
                // Over the whole trie, every node is within the range deleted above.
                if (from == ValueKeccak.Zero && to == ValueKeccak.MaxValue) continue;

                DeleteNodesReachingInto(reader, batch, accountPath.ToCommitment(), from, to, wholePath: true);
            }
        }

        if (_logger.IsInfo) _logger.Info($"Resuming the snap sync from a checkpoint; discarded what was written after it in {Stopwatch.GetElapsedTime(startTime).TotalSeconds:N1}s.");
    }

    /// <summary>
    /// Deletes the nodes on the paths to <paramref name="from"/> and <paramref name="to"/> whose keys reach into
    /// <c>[<paramref name="from"/>, <paramref name="to"/>]</c>, or whose path does when <paramref name="wholePath"/> is
    /// set, in the storage trie of <paramref name="address"/>, or in the state trie when it is <c>null</c>.
    /// </summary>
    private static void DeleteNodesReachingInto(IPersistence.IPersistenceReader reader, IPersistence.IWriteBatch batch, Hash256? address, in ValueHash256 from, in ValueHash256 to, bool wholePath)
    {
        DeleteNodesReachingInto(reader, batch, address, new TreePath(from, MaxNodePathLength), from, to, wholePath);
        DeleteNodesReachingInto(reader, batch, address, new TreePath(to, MaxNodePathLength), from, to, wholePath);
    }

    private static void DeleteNodesReachingInto(IPersistence.IPersistenceReader reader, IPersistence.IWriteBatch batch, Hash256? address, in TreePath boundary, in ValueHash256 from, in ValueHash256 to, bool wholePath)
    {
        for (int length = 0; length < MaxNodePathLength; length++)
        {
            TreePath path = boundary.Truncate(length);
            TreePath keys = path;
            bool found = address is null
                ? reader.TryLoadStateRlp(path, ReadFlags.None, AppendNodeKey, new Span<TreePath>(ref keys))
                : reader.TryLoadStorageRlp(address, path, ReadFlags.None, AppendNodeKey, new Span<TreePath>(ref keys));
            if (!found) continue;

            if (!wholePath && (keys.ToUpperBoundPath().CompareTo(from) < 0 || keys.ToLowerBoundPath().CompareTo(to) > 0)) continue;

            // A null span deletes the key.
            if (address is null) batch.SetStateTrieNode(path, default);
            else batch.SetStorageTrieNode(address, path, default);
        }
    }

    /// <summary>Appends the key nibbles of the leaf or extension node <paramref name="rlp"/> to the path in <paramref name="keys"/>; a branch has no key.</summary>
    private static void AppendNodeKey(ReadOnlySpan<byte> rlp, Span<TreePath> keys)
    {
        NodeRlpReader reader = new(rlp);
        int position = 0;
        reader.ReadSequenceLength(ref position, out _);
        int keyPosition = position;
        reader.SkipItems(ref position, 2);
        if (position != rlp.Length) return;

        reader.DecodeByteArraySpan(ref keyPosition, out ReadOnlySpan<byte> hexPrefix);
        Span<byte> nibbles = stackalloc byte[NodeRlp.HexPrefixNibbleCount(hexPrefix)];
        NodeRlp.ReadHexPrefix(hexPrefix, 0, nibbles);
        keys[0] = keys[0].Append(nibbles);
    }

    public ISnapTree<PathWithAccount> CreateStateTree()
    {
        IPersistence.IPersistenceReader reader = new LazyReader(persistence, ReaderFlags.Sync);
        IPersistence.IWriteBatch writeBatch = persistence.CreateWriteBatch(StateId.Sync, StateId.Sync, WriteFlags.DisableWAL);
        return new FlatSnapStateTree(reader, writeBatch, syncConfig.EnableSnapDoubleWriteCheck, logManager)
        {
            BeforeCleanup = syncConfig.SnapResume ? InvalidateCheckpointForRange : null
        };
    }

    public ISnapTree<PathWithStorageSlot> CreateStorageTree(in ValueHash256 accountPath)
    {
        IPersistence.IPersistenceReader reader = new LazyReader(persistence, ReaderFlags.Sync);
        IPersistence.IAtomicPersistenceBatch? writeBatch = null;
        try
        {
            writeBatch = new LazyAtomicWriteBatch(persistence);
            return new FlatSnapStorageTree(reader, writeBatch, accountPath.ToCommitment(), syncConfig.EnableSnapDoubleWriteCheck, logManager)
            {
                BeforeCleanup = HasSavedCheckpoint ? _invalidateStorageCheckpoint ??= InvalidateSavedCheckpoint : null,
            };
        }
        catch
        {
            try
            {
                writeBatch?.Dispose();
            }
            finally
            {
                reader.Dispose();
            }
            throw;
        }
    }

    /// <summary>
    /// Defers the atomic write batch, and the snapshot its range deletes scan, until first use.
    /// </summary>
    /// <remarks>
    /// <see cref="IsStorageRangeEmpty"/> creates the inner batch like any other call, so the probe and the range deletes
    /// it lets the tree skip read one snapshot, and its answer cannot differ from what those deletes would find. In a
    /// storage tree the probe is the first call: the boundary cleanup it guards runs before the commit writes anything,
    /// and the batch's own pending writes are never in its snapshot anyway. Taking the snapshot then rather than at tree
    /// creation only lets sibling commits made in between show, which is safe for the reason given on
    /// <see cref="LazyReader"/>.
    /// </remarks>
    private sealed class LazyAtomicWriteBatch(IPersistence persistence) : IPersistence.IAtomicPersistenceBatch
    {
        private IPersistence.IAtomicPersistenceBatch? _inner;
        private bool _disposed;

        private IPersistence.IAtomicPersistenceBatch Inner
        {
            get
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _inner ??= persistence.CreateAtomicWriteBatch(StateId.Sync, StateId.Sync, WriteFlags.DisableWAL);
            }
        }

        public void SelfDestruct(Address addr) => Inner.SelfDestruct(addr);
        public void SetAccount(Address addr, Account? account) => Inner.SetAccount(addr, account);
        public void SetSlimAccount(Address addr, scoped ReadOnlySpan<byte> slimRlp) => Inner.SetSlimAccount(addr, slimRlp);
        public void SetStorage(Address addr, in UInt256 slot, in UInt256? value) => Inner.SetStorage(addr, slot, value);
        public void SetStateTrieNode(in TreePath path, scoped ReadOnlySpan<byte> rlp) => Inner.SetStateTrieNode(path, rlp);
        public void SetStorageTrieNode(in ValueHash256 address, in TreePath path, scoped ReadOnlySpan<byte> rlp) => Inner.SetStorageTrieNode(address, path, rlp);
        public void DeleteStateTrieNode(in TreePath path) => Inner.DeleteStateTrieNode(path);
        public void DeleteStorageTrieNode(in ValueHash256 address, in TreePath path) => Inner.DeleteStorageTrieNode(address, path);
        public void SetStorageRawEncoded(in ValueHash256 addrHash, in ValueHash256 slotHash, scoped ReadOnlySpan<byte> rlpValue) => Inner.SetStorageRawEncoded(addrHash, slotHash, rlpValue);
        public void SetAccountRaw(in ValueHash256 addrHash, in AccountStruct account) => Inner.SetAccountRaw(addrHash, account);
        public void DeleteAccountRange(in ValueHash256 fromPath, in ValueHash256 toPath) => Inner.DeleteAccountRange(fromPath, toPath);
        public void DeleteStorageRange(in ValueHash256 addressHash, in ValueHash256 fromPath, in ValueHash256 toPath) => Inner.DeleteStorageRange(addressHash, fromPath, toPath);
        public void SelfDestructRange(in ValueHash256 fromPath, in ValueHash256 toPath, in ValueHash256? keptAccount) => Inner.SelfDestructRange(fromPath, toPath, keptAccount);
        public void DeleteStateTrieNodeRange(in ValueHash256 from, in ValueHash256 to) => Inner.DeleteStateTrieNodeRange(from, to);
        public void DeleteStorageTrieNodeRange(in ValueHash256 addressHash, in ValueHash256 from, in ValueHash256 to, Action? beforeDelete = null) => Inner.DeleteStorageTrieNodeRange(addressHash, from, to, beforeDelete);
        public bool IsStorageRangeEmpty(in ValueHash256 addressHash, in ValueHash256 from, in ValueHash256 to) => Inner.IsStorageRangeEmpty(addressHash, from, to);
        public void SetMigrationCursor(in MigrationCursor? cursor) => Inner.SetMigrationCursor(cursor);

        public void Commit()
        {
            try
            {
                Inner.Commit();
            }
            finally
            {
                Dispose();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _inner?.Dispose();
        }
    }

    /// <summary>
    /// Defers reader (and its underlying DB snapshot) creation until first use.
    /// </summary>
    /// <remarks>
    /// A snap tree is created once per account per storage response, but its reader is only ever
    /// touched on the proof/boundary-stitching and double-write-check paths — never in the dominant
    /// proofless path — so creating the snapshot eagerly is wasted work.
    /// <para>
    /// Deferral moves the snapshot point from tree creation to the first read, so concurrent
    /// responses committed in between become visible. This is safe because tree creation was never
    /// ordered with respect to sibling commits: any state observable through the later snapshot is
    /// exactly the state an eager snapshot would have observed under a legal alternative scheduling,
    /// so no reader can depend on the earlier point. One visible consequence: the debug-only
    /// <see cref="ISyncConfig.EnableSnapDoubleWriteCheck"/> is now more likely to observe a write
    /// committed by a concurrent sibling range of the same account (benign, but it throws).
    /// </para>
    /// Not thread-safe, matching the single-threaded use of each snap tree — a racing first read
    /// would create two inner readers and leak one native snapshot, not just return a stale value.
    /// Disposes the inner reader only if it was created.
    /// </remarks>
    private sealed class LazyReader(IPersistence persistence, ReaderFlags readerFlags) : IPersistence.IPersistenceReader
    {
        private IPersistence.IPersistenceReader? _inner;
        private bool _disposed;

        private IPersistence.IPersistenceReader Inner
        {
            get
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _inner ??= persistence.CreateReader(readerFlags);
            }
        }

        public StateId CurrentState => Inner.CurrentState;
        public bool IsPreimageMode => Inner.IsPreimageMode;
        public Account? GetAccount(Address address) => Inner.GetAccount(address);
        public bool TryGetSlot(Address address, in UInt256 slot, ref UInt256 outValue) => Inner.TryGetSlot(address, slot, ref outValue);
        public MigrationCursor? GetMigrationCursor() => Inner.GetMigrationCursor();
        public bool TryLoadStateRlp<TArg>(in TreePath path, ReadFlags flags, ReadOnlySpanAction<byte, TArg> action, TArg arg) where TArg : allows ref struct => Inner.TryLoadStateRlp(path, flags, action, arg);
        public bool TryLoadStorageRlp<TArg>(Hash256 address, in TreePath path, ReadFlags flags, ReadOnlySpanAction<byte, TArg> action, TArg arg) where TArg : allows ref struct => Inner.TryLoadStorageRlp(address, path, flags, action, arg);
        public bool TryLoadStateRlp<TArg>(in TreePath path, in ValueHash256 hash, ReadFlags flags, ReadOnlySpanAction<byte, TArg> action, TArg arg) where TArg : allows ref struct => Inner.TryLoadStateRlp(path, hash, flags, action, arg);
        public bool TryLoadStorageRlp<TArg>(in ValueHash256 address, in TreePath path, in ValueHash256 hash, ReadFlags flags, ReadOnlySpanAction<byte, TArg> action, TArg arg) where TArg : allows ref struct => Inner.TryLoadStorageRlp(address, path, hash, flags, action, arg);
        public byte[]? GetAccountRaw(in ValueHash256 addrHash) => Inner.GetAccountRaw(addrHash);
        public bool TryGetStorageRaw(in ValueHash256 addrHash, in ValueHash256 slotHash, ref UInt256 value) => Inner.TryGetStorageRaw(addrHash, slotHash, ref value);
        public IPersistence.IFlatIterator CreateAccountIterator(in ValueHash256 startKey, in ValueHash256 endKey) => Inner.CreateAccountIterator(startKey, endKey);
        public IPersistence.IFlatIterator CreateStorageIterator(in ValueHash256 accountKey, in ValueHash256 startSlotKey, in ValueHash256 endSlotKey) => Inner.CreateStorageIterator(accountKey, startSlotKey, endSlotKey);
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _inner?.Dispose();
        }
    }
}
