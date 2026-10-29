// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Diagnostics;
using Autofac.Features.AttributeFilters;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Snap;
using Nethermind.Synchronization.SnapSync;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

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
    private const int AccountKeyLength = 20;
    private const int MaxNodePathLength = 64;

    private readonly ILogger _logger = logManager.GetClassLogger<FlatSnapTrieFactory>();

    public void EnsureInitialize()
    {
        if (syncConfig.SnapResume && LoadCheckpoint() is { } checkpoint)
        {
            DiscardWritesAfter(checkpoint);
            persistenceManager.ResetPersistedStateId();
            return;
        }

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

    private bool HasAccounts()
    {
        using IPersistence.IPersistenceReader reader = persistence.CreateReader();
        using IPersistence.IFlatIterator accounts = reader.CreateAccountIterator(ValueKeccak.Zero, ValueKeccak.MaxValue);
        return accounts.MoveNext();
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
                DeleteNodesReachingInto(reader, batch, address: null, partition.NextAccountPath, partition.AccountPathLimit);

                if (TryGetFirstAccountKeyAtOrAfter(partition.NextAccountPath, out ValueHash256 firstAccount))
                {
                    batch.DeleteAccountRange(firstAccount, partition.AccountPathLimit);
                    batch.SelfDestructRange(firstAccount, partition.AccountPathLimit);
                }
            }

            foreach ((ValueHash256 accountPath, ValueHash256 from, ValueHash256 to) in checkpoint.StorageRangesToRefetch())
            {
                batch.DeleteStorageRange(accountPath, from, to);
                batch.DeleteStorageTrieNodeRange(accountPath, from, to);
                // Over the whole trie, every node is within the range deleted above.
                if (from == ValueKeccak.Zero && to == ValueKeccak.MaxValue) continue;

                DeleteNodesReachingInto(reader, batch, accountPath.ToCommitment(), from, to);
            }
        }

        if (_logger.IsInfo) _logger.Info($"Resuming the snap sync from a checkpoint; discarded what was written after it in {Stopwatch.GetElapsedTime(startTime).TotalSeconds:N1}s.");
    }

    /// <summary>
    /// Deletes the nodes on the paths to <paramref name="from"/> and <paramref name="to"/> whose keys reach into
    /// <c>[<paramref name="from"/>, <paramref name="to"/>]</c>, in the storage trie of <paramref name="address"/>, or in
    /// the state trie when it is <c>null</c>.
    /// </summary>
    private static void DeleteNodesReachingInto(IPersistence.IPersistenceReader reader, IPersistence.IWriteBatch batch, Hash256? address, in ValueHash256 from, in ValueHash256 to)
    {
        DeleteNodesReachingInto(reader, batch, address, new TreePath(from, MaxNodePathLength), from, to);
        DeleteNodesReachingInto(reader, batch, address, new TreePath(to, MaxNodePathLength), from, to);
    }

    private static void DeleteNodesReachingInto(IPersistence.IPersistenceReader reader, IPersistence.IWriteBatch batch, Hash256? address, in TreePath boundary, in ValueHash256 from, in ValueHash256 to)
    {
        for (int length = 0; length < MaxNodePathLength; length++)
        {
            TreePath path = boundary.Truncate(length);
            byte[]? rlp = address is null
                ? reader.TryLoadStateRlp(path, ReadFlags.None)
                : reader.TryLoadStorageRlp(address, path, ReadFlags.None);
            if (rlp is null) continue;

            TrieNode node = new(NodeType.Unknown, rlp);
            node.ResolveNode(NullTrieNodeResolver.Instance, path);
            TreePath keys = node.IsBranch ? path : path.Append(node.Key);
            if (keys.ToUpperBoundPath().CompareTo(from) < 0 || keys.ToLowerBoundPath().CompareTo(to) > 0) continue;

            // A null span deletes the key.
            if (address is null) batch.SetStateTrieNode(path, default);
            else batch.SetStorageTrieNode(address, path, default);
        }
    }

    /// <summary>Finds the lowest path whose 20-byte account key no account below <paramref name="path"/> can share.</summary>
    /// <remarks>
    /// Accounts and their storage are keyed by the first 20 bytes of the path, so a range deleted from
    /// <paramref name="path"/> itself would also take the account just below it, which shares those bytes.
    /// </remarks>
    /// <returns><c>false</c> when no such path exists.</returns>
    private static bool TryGetFirstAccountKeyAtOrAfter(in ValueHash256 path, out ValueHash256 firstAccount)
    {
        firstAccount = path;
        Span<byte> bytes = firstAccount.BytesAsSpan;
        if (!bytes[AccountKeyLength..].ContainsAnyExcept((byte)0)) return true;

        bytes[AccountKeyLength..].Clear();
        for (int i = AccountKeyLength - 1; i >= 0; i--)
        {
            if (++bytes[i] != 0) return true;
        }

        return false;
    }

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
        public byte[]? TryLoadStateRlp(in TreePath path, ReadFlags flags) => Inner.TryLoadStateRlp(path, flags);
        public byte[]? TryLoadStorageRlp(Hash256 address, in TreePath path, ReadFlags flags) => Inner.TryLoadStorageRlp(address, path, flags);
        public bool TryLoadStateRlp<TArg>(in TreePath path, ReadFlags flags, ReadOnlySpanAction<byte, TArg> action, TArg arg) where TArg : allows ref struct => Inner.TryLoadStateRlp(path, flags, action, arg);
        public bool TryLoadStorageRlp<TArg>(Hash256 address, in TreePath path, ReadFlags flags, ReadOnlySpanAction<byte, TArg> action, TArg arg) where TArg : allows ref struct => Inner.TryLoadStorageRlp(address, path, flags, action, arg);
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
