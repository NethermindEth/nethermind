// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.Blockchain.PartialArchive;

/// <summary>
/// Maintains the bookkeeping that makes a rolling window of historical state possible: a
/// per-path latest-version index and a per-block expiry journal of superseded node keys, and
/// executes the window prune that deletes superseded keys once they fall out of the retention
/// window.
/// </summary>
/// <remarks>
/// With the HalfPath key scheme, node keys are qualified by the node keccak, so all versions of
/// a path coexist on disk and historical state reads work through the regular
/// <see cref="INodeStorage"/> as long as superseded keys are not deleted. This class receives
/// <see cref="IPersistedNodeObserver"/> events from the <see cref="TrieStore"/>, derives which
/// previously persisted key each write supersedes, and journals it under the superseding block
/// number. Pruning then deletes journal entries whose block left the window, guarded against
/// resurrected nodes (a re-created node with an identical keccak must not be deleted).
/// All index reads and writes, including prune execution, happen on a single consumer task, so
/// event application and prune verification are strictly ordered. Prune slices additionally run
/// only inside the persistence barrier (<see cref="OnSnapshotPersisted"/>), while the
/// persistence thread is parked waiting for the drain — so a node-key deletion can never race a
/// concurrent persistence write of the same re-created key.
/// Failure bias is towards leaking keys (never deleting) rather than deleting live state: on any
/// tracking gap that could make deletion unsafe the tracker poisons itself durably and pruning
/// halts until the state is resynced.
/// </remarks>
public sealed class PartialArchiveNodeTracker : IPersistedNodeObserver, IDisposable
{
    private const int ChannelCapacity = 1 << 20;
    private const int FlushThreshold = 1 << 15;
    internal const int PruneRowBudget = 200_000;
    private static readonly TimeSpan BarrierTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan DisposeTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PersistEnqueueTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RecommitEnqueueTimeout = TimeSpan.FromMilliseconds(250);

    private static readonly byte[] FloorKey = [1];
    private static readonly byte[] LastSnapshotBlockKey = [2];
    private static readonly byte[] PoisonedKey = [3];

    // Written to both the state DB and tracker metadata; full pruning/resync drops the state-side
    // copy, signalling that the tracking data is stale and must be reset.
    internal static readonly byte[] StateStampKey = Keccak.Compute("PartialArchiveStateStamp").BytesToArray();

    private readonly IColumnsDb<PartialArchiveColumns> _db;
    private readonly IDb _mapDb;
    private readonly IDb _supersededDb;
    private readonly IDb _journalDb;
    private readonly IDb _metadataDb;
    private readonly INodeStorage _nodeStorage;
    private readonly ILogger _logger;

    private readonly Channel<Message> _messages = Channel.CreateBounded<Message>(
        new BoundedChannelOptions(ChannelCapacity) { SingleReader = true, SingleWriter = false });
    private readonly Task _consumerTask;

    // Read-your-own-writes view of index entries buffered in the current write batch.
    private readonly Dictionary<byte[], byte[]> _pendingMap = new(Bytes.EqualityComparer);
    private readonly Dictionary<byte[], byte[]?> _pendingSuperseded = new(Bytes.EqualityComparer);
    private IColumnsWriteBatch<PartialArchiveColumns>? _batch;
    private IWriteBatch? _mapBatch;
    private IWriteBatch? _supersededBatch;
    private IWriteBatch? _journalBatch;
    private int _batchedWrites;

    private volatile bool _disposed;
    private volatile bool _poisoned;
    private ulong _lastSnapshotBlock;
    private ulong _oldestRetainedBlock;
    private ulong _pendingPruneCutoff;
    private long _droppedPersistEvents;

    public PartialArchiveNodeTracker(
        IColumnsDb<PartialArchiveColumns> db,
        IDbProvider dbProvider,
        INodeStorage nodeStorage,
        ILogManager logManager)
    {
        _db = db;
        _mapDb = db.GetColumnDb(PartialArchiveColumns.LatestVersion);
        _supersededDb = db.GetColumnDb(PartialArchiveColumns.SupersededAt);
        _journalDb = db.GetColumnDb(PartialArchiveColumns.ExpiryJournal);
        _metadataDb = db.GetColumnDb(PartialArchiveColumns.Metadata);
        _nodeStorage = nodeStorage;
        _logger = logManager.GetClassLogger<PartialArchiveNodeTracker>();

        EnsureStateDatabaseIdentity(dbProvider.StateDb);

        _lastSnapshotBlock = ReadMetadataUlong(LastSnapshotBlockKey) ?? 0;
        _oldestRetainedBlock = ReadMetadataUlong(FloorKey) ?? 0;
        _poisoned = _metadataDb.Get(PoisonedKey) is not null;
        if (_poisoned && _logger.IsError)
        {
            _logger.Error("Partial archive tracking was previously poisoned; window pruning stays disabled. Resync or full-prune the state to recover.");
        }

        _consumerTask = Task.Run(RunConsumer);
    }

    /// <summary>Oldest block whose historical state is still fully retained, or 0 when pruning has not run yet.</summary>
    public ulong OldestRetainedBlock => Volatile.Read(ref _oldestRetainedBlock);

    /// <summary>Highest block covered by a completed persistence pass; prune cutoffs are capped by it.</summary>
    public ulong LastSnapshotBlock => Volatile.Read(ref _lastSnapshotBlock);

    /// <summary>Whether tracking hit an unrecoverable gap; when set, pruning is permanently halted.</summary>
    public bool IsPoisoned => _poisoned;

    public void OnNodePersisted(Hash256? address, in TreePath path, Hash256 keccak, ulong blockNumber)
    {
        if (_disposed || _poisoned) return;

        Message message = Message.Node(address, in path, keccak, blockNumber);
        if (_messages.Writer.TryWrite(message)) return;

        // Persistence threads run in the background; wait rather than lose supersession info.
        // Losing a persist event only leaks a key, so give up with a warning after a deadline.
        long deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * (long)PersistEnqueueTimeout.TotalSeconds;
        SpinWait spin = default;
        while (!_disposed && !_poisoned && Stopwatch.GetTimestamp() < deadline)
        {
            spin.SpinOnce();
            if (_messages.Writer.TryWrite(message)) return;
        }

        if (Interlocked.Increment(ref _droppedPersistEvents) == 1 && _logger.IsWarn)
        {
            _logger.Warn("Partial archive tracker is not keeping up with node persistence; some superseded keys will not be reclaimed.");
        }
    }

    public void OnNodeRecommitted(Hash256? address, in TreePath path, Hash256 keccak, ulong blockNumber)
    {
        if (_disposed || _poisoned) return;

        Message message = Message.Node(address, in path, keccak, blockNumber);
        if (_messages.Writer.TryWrite(message)) return;

        // Called on the block-processing path. A lost recommit can make the index treat a live
        // node as superseded, after which deleting it would corrupt state — so ride out a
        // transient backlog with a short bounded wait, and only then permanently disable pruning.
        long deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * (long)RecommitEnqueueTimeout.TotalMilliseconds / 1000;
        SpinWait spin = default;
        while (!_disposed && !_poisoned && Stopwatch.GetTimestamp() < deadline)
        {
            spin.SpinOnce();
            if (_messages.Writer.TryWrite(message)) return;
        }

        if (!_disposed && !_poisoned)
        {
            Poison($"a node recommit event could not be enqueued within {RecommitEnqueueTimeout.TotalMilliseconds}ms (queue full)", null);
        }
    }

    public void OnSnapshotPersisted(ulong lastPersistedBlockNumber)
    {
        if (_disposed || _poisoned) return;

        ManualResetEventSlim barrier = new(false);
        try
        {
            if (!TryWriteBlocking(Message.BarrierMessage(barrier, lastPersistedBlockNumber))) return;
            if (!barrier.Wait(BarrierTimeout) && _logger.IsWarn)
            {
                _logger.Warn($"Partial archive tracker did not drain within {BarrierTimeout.TotalSeconds}s after persisting up to block {lastPersistedBlockNumber}.");
            }
        }
        finally
        {
            barrier.Dispose();
        }
    }

    /// <summary>
    /// Requests deletion of superseded node keys journaled at or below <paramref name="cutoffBlockNumber"/>.
    /// </summary>
    /// <remarks>
    /// Deletion is deferred to the next persistence barrier (<see cref="OnSnapshotPersisted"/>):
    /// at that point the persistence thread is parked waiting for the tracker to drain, so a
    /// prune's node-key deletions can never race a concurrent persistence write of the same
    /// re-created key. Large backlogs are processed in bounded slices across barriers.
    /// </remarks>
    public bool RequestPrune(ulong cutoffBlockNumber)
    {
        if (_disposed || _poisoned) return false;

        ulong current = Volatile.Read(ref _pendingPruneCutoff);
        while (cutoffBlockNumber > current)
        {
            ulong seen = Interlocked.CompareExchange(ref _pendingPruneCutoff, cutoffBlockNumber, current);
            if (seen == current) break;
            current = seen;
        }

        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _messages.Writer.TryComplete();
        try
        {
            if (!_consumerTask.Wait(DisposeTimeout) && _logger.IsWarn)
            {
                _logger.Warn("Partial archive tracker consumer did not stop in time.");
            }
        }
        catch (AggregateException e)
        {
            if (_logger.IsError) _logger.Error("Partial archive tracker consumer failed during shutdown.", e);
        }
    }

    private bool TryWriteBlocking(in Message message)
    {
        if (_messages.Writer.TryWrite(message)) return true;
        SpinWait spin = default;
        while (!_disposed)
        {
            spin.SpinOnce();
            if (_messages.Writer.TryWrite(message)) return true;
        }

        return false;
    }

    private async Task RunConsumer()
    {
        await foreach (Message message in _messages.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                if (_poisoned)
                {
                    SignalBarrier(message);
                    continue;
                }

                switch (message.Kind)
                {
                    case MessageKind.Node:
                        ProcessNode(in message);
                        if (_batchedWrites >= FlushThreshold) FlushBatch();
                        break;
                    case MessageKind.Barrier:
                        FlushBatch();
                        UpdateLastSnapshotBlock(message.BlockNumber);
                        // Prune while the persistence thread is parked on this barrier: node-key
                        // deletions cannot race a persistence write of the same re-created key.
                        ExecutePendingPruneSlice();
                        SignalBarrier(message);
                        break;
                }
            }
            catch (Exception e)
            {
                Poison($"processing failed: {e.Message}", e);
                SignalBarrier(message);
            }
        }

        try
        {
            FlushBatch();
        }
        catch (Exception e)
        {
            long dropped = Interlocked.Read(ref _droppedPersistEvents);
            if (_logger.IsWarn) _logger.Warn($"Partial archive tracker final flush failed ({dropped} events dropped): {e}");
        }
    }

    private void SignalBarrier(in Message message)
    {
        try
        {
            message.Barrier?.Set();
        }
        catch (ObjectDisposedException)
        {
            // The waiter timed out and disposed the barrier; nothing left to signal.
        }
    }

    private void ProcessNode(in Message message)
    {
        Span<byte> keyBuffer = stackalloc byte[PartialArchiveKeys.MaxPathKeyLength];
        TreePath path = message.Path;
        int keyLength = PartialArchiveKeys.WritePathKey(keyBuffer, message.Address, in path);
        byte[] pathKey = keyBuffer[..keyLength].ToArray();

        if (!_pendingMap.TryGetValue(pathKey, out byte[]? currentValue))
        {
            currentValue = _mapDb.Get(pathKey);
        }

        Hash256 keccak = message.Keccak!;
        if (currentValue is null)
        {
            // First version seen for this path. The pre-existing (e.g. snap-synced) version, if
            // any, is unknown and intentionally never journaled: bias towards leaking a key over
            // deleting one that may still be referenced.
            WriteMapEntry(pathKey, keccak, message.BlockNumber);
            return;
        }

        (ValueHash256 currentKeccak, ulong currentBlock) = PartialArchiveKeys.ReadVersionValue(currentValue);
        if (currentKeccak == keccak.ValueHash256)
        {
            if (message.BlockNumber > currentBlock)
            {
                WriteMapEntry(pathKey, keccak, message.BlockNumber);
            }
            return;
        }

        if (message.BlockNumber >= currentBlock)
        {
            Supersede(pathKey, in currentKeccak, message.BlockNumber);
            WriteMapEntry(pathKey, keccak, message.BlockNumber);
        }
        else
        {
            // Events can arrive out of block order (commit-time recommits overtake persistence);
            // the reported version is the older one, superseded at the currently indexed block.
            Supersede(pathKey, keccak.ValueHash256, currentBlock);
        }
    }

    /// <summary>
    /// Records that the node version <paramref name="keccak"/> at this path was superseded at
    /// <paramref name="blockNumber"/>. Only the latest supersession per node key is kept: a
    /// re-created version moves forward, so it is never deleted while any block inside the
    /// retention window can still reference it.
    /// </summary>
    private void Supersede(byte[] pathKey, in ValueHash256 keccak, ulong blockNumber)
    {
        Span<byte> keyBuffer = stackalloc byte[PartialArchiveKeys.MaxPathKeyLength + 32];
        int length = PartialArchiveKeys.WriteSupersededKey(keyBuffer, pathKey, in keccak);
        byte[] supersededKey = keyBuffer[..length].ToArray();

        if (!_pendingSuperseded.TryGetValue(supersededKey, out byte[]? previousValue))
        {
            previousValue = _supersededDb.Get(supersededKey);
        }

        if (previousValue is not null)
        {
            ulong previousBlock = PartialArchiveKeys.ReadBlockNumberValue(previousValue);
            if (previousBlock == blockNumber) return;
            RemoveJournalRow(previousBlock, supersededKey);
        }

        EnsureBatch();
        _supersededBatch!.Set(supersededKey, PartialArchiveKeys.BlockNumberValue(blockNumber));
        _pendingSuperseded[supersededKey] = PartialArchiveKeys.BlockNumberValue(blockNumber);
        AppendJournalRow(blockNumber, supersededKey);
        _batchedWrites += 2;
    }

    private void AppendJournalRow(ulong blockNumber, ReadOnlySpan<byte> supersededKey)
    {
        Span<byte> keyBuffer = stackalloc byte[PartialArchiveKeys.MaxJournalKeyLength];
        BinaryPrimitives.WriteUInt64BigEndian(keyBuffer, blockNumber);
        supersededKey.CopyTo(keyBuffer[sizeof(ulong)..]);
        EnsureBatch();
        _journalBatch!.Set(keyBuffer[..(sizeof(ulong) + supersededKey.Length)], []);
    }

    private void RemoveJournalRow(ulong blockNumber, ReadOnlySpan<byte> supersededKey)
    {
        Span<byte> keyBuffer = stackalloc byte[PartialArchiveKeys.MaxJournalKeyLength];
        BinaryPrimitives.WriteUInt64BigEndian(keyBuffer, blockNumber);
        supersededKey.CopyTo(keyBuffer[sizeof(ulong)..]);
        EnsureBatch();
        _journalBatch!.Remove(keyBuffer[..(sizeof(ulong) + supersededKey.Length)]);
    }

    private void WriteMapEntry(byte[] pathKey, Hash256 keccak, ulong blockNumber)
    {
        byte[] value = new byte[PartialArchiveKeys.VersionValueLength];
        PartialArchiveKeys.WriteVersionValue(value, keccak, blockNumber);
        EnsureBatch();
        _mapBatch!.Set(pathKey, value);
        _pendingMap[pathKey] = value;
        _batchedWrites++;
    }

    private void EnsureBatch()
    {
        if (_batch is not null) return;
        _batch = _db.StartWriteBatch();
        _mapBatch = _batch.GetColumnBatch(PartialArchiveColumns.LatestVersion);
        _supersededBatch = _batch.GetColumnBatch(PartialArchiveColumns.SupersededAt);
        _journalBatch = _batch.GetColumnBatch(PartialArchiveColumns.ExpiryJournal);
    }

    private void FlushBatch()
    {
        if (_batch is null) return;
        _mapBatch = null;
        _supersededBatch = null;
        _journalBatch = null;
        IColumnsWriteBatch<PartialArchiveColumns> batch = _batch;
        _batch = null;
        batch.Dispose();
        _pendingMap.Clear();
        _pendingSuperseded.Clear();
        _batchedWrites = 0;
    }

    private void UpdateLastSnapshotBlock(ulong blockNumber)
    {
        if (blockNumber > Volatile.Read(ref _lastSnapshotBlock))
        {
            Volatile.Write(ref _lastSnapshotBlock, blockNumber);
            WriteMetadataUlong(LastSnapshotBlockKey, blockNumber);
        }
    }

    private void ExecutePendingPruneSlice()
    {
        ulong requestedCutoff = Volatile.Read(ref _pendingPruneCutoff);
        if (requestedCutoff == 0) return;

        // Journals are complete only up to the last finished persistence pass.
        ulong cutoff = Math.Min(requestedCutoff, Volatile.Read(ref _lastSnapshotBlock));
        if (cutoff == 0) return;

        long startTimestamp = Stopwatch.GetTimestamp();
        int deleted = 0;
        int resurrected = 0;
        int scanned = 0;
        ulong floor = Volatile.Read(ref _oldestRetainedBlock);
        bool exhausted = true;

        INodeStorage.IWriteBatch nodeBatch = _nodeStorage.StartWriteBatch();
        IColumnsWriteBatch<PartialArchiveColumns> archiveBatch = _db.StartWriteBatch();
        try
        {
            IWriteBatch journalBatch = archiveBatch.GetColumnBatch(PartialArchiveColumns.ExpiryJournal);
            IWriteBatch supersededBatch = archiveBatch.GetColumnBatch(PartialArchiveColumns.SupersededAt);
            foreach (KeyValuePair<byte[], byte[]?> row in _journalDb.GetAll(ordered: true))
            {
                byte[] journalKey = row.Key;
                ulong blockNumber = PartialArchiveKeys.ReadJournalBlockNumber(journalKey);
                if (blockNumber > cutoff) break;
                if (++scanned > PruneRowBudget)
                {
                    exhausted = false;
                    break;
                }

                if (PartialArchiveKeys.TryParseJournalKey(journalKey, out _, out Hash256? address, out TreePath path, out ValueHash256 keccak))
                {
                    byte[]? currentValue = _mapDb.Get(PartialArchiveKeys.JournalPathKey(journalKey));
                    if (currentValue is not null && PartialArchiveKeys.ReadVersionValue(currentValue).Keccak == keccak)
                    {
                        // The exact same node was re-created after being superseded; it is the
                        // current version again and must be kept.
                        resurrected++;
                    }
                    else
                    {
                        nodeBatch.Set(address, in path, in keccak, default, WriteFlags.LowPriority);
                        deleted++;
                    }
                }

                supersededBatch.Remove(PartialArchiveKeys.JournalKeyToSupersededKey(journalKey));
                journalBatch.Remove(journalKey);
                floor = blockNumber;
            }

            if (floor > Volatile.Read(ref _oldestRetainedBlock))
            {
                // In the same batch as the journal removal: the advertised floor may never get
                // ahead of what was actually pruned, even across a crash.
                Span<byte> floorValue = stackalloc byte[sizeof(ulong)];
                BinaryPrimitives.WriteUInt64BigEndian(floorValue, floor);
                archiveBatch.GetColumnBatch(PartialArchiveColumns.Metadata).Set(FloorKey, floorValue.ToArray());
            }
        }
        finally
        {
            // Node deletions land before journal rows are removed: a crash in between only causes
            // idempotent re-deletion on the next pass.
            nodeBatch.Dispose();
            archiveBatch.Dispose();
        }

        if (floor > Volatile.Read(ref _oldestRetainedBlock))
        {
            Volatile.Write(ref _oldestRetainedBlock, floor);
        }

        if (_logger.IsInfo && scanned > 0)
        {
            long elapsedMs = (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            _logger.Info($"Partial archive prune: deleted {deleted} superseded node keys ({resurrected} kept as resurrected), retention floor {floor}, cutoff {cutoff}, took {elapsedMs}ms.");
        }

        if (exhausted)
        {
            // Newer requests may have arrived while pruning; only clear the one we satisfied.
            Interlocked.CompareExchange(ref _pendingPruneCutoff, 0, requestedCutoff);
        }
    }

    private void Poison(string reason, Exception? exception)
    {
        if (_poisoned) return;
        _poisoned = true;
        if (_logger.IsError)
        {
            _logger.Error($"Partial archive tracking disabled: {reason}. Window pruning is halted; superseded state will no longer be deleted. Resync or full-prune the state to recover.", exception);
        }

        try
        {
            _metadataDb.Set(PoisonedKey, [1]);
        }
        catch (Exception e)
        {
            if (_logger.IsError) _logger.Error("Failed to persist the partial archive poison marker.", e);
        }
    }

    private void EnsureStateDatabaseIdentity(IKeyValueStore stateDb)
    {
        byte[]? stateStamp = stateDb[StateStampKey];
        byte[]? trackedStamp = _metadataDb.Get(StateStampKey);
        if (stateStamp is not null && trackedStamp is not null && Bytes.AreEqual(stateStamp, trackedStamp)) return;

        if (trackedStamp is not null || _metadataDb.Get(FloorKey) is not null || _metadataDb.Get(LastSnapshotBlockKey) is not null)
        {
            _mapDb.Clear();
            _supersededDb.Clear();
            _journalDb.Clear();
            _metadataDb.Clear();
            if (_logger.IsInfo) _logger.Info("State database changed since partial archive tracking data was written (full pruning or resync); tracking was reset.");
        }

        byte[] stamp = stateStamp ?? Guid.NewGuid().ToByteArray();
        stateDb[StateStampKey] = stamp;
        _metadataDb.Set(StateStampKey, stamp);
    }

    private ulong? ReadMetadataUlong(byte[] key)
    {
        byte[]? value = _metadataDb.Get(key);
        return value is { Length: sizeof(ulong) } ? BinaryPrimitives.ReadUInt64BigEndian(value) : null;
    }

    private void WriteMetadataUlong(byte[] key, ulong value)
    {
        byte[] buffer = new byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(buffer, value);
        _metadataDb.Set(key, buffer);
    }

    private enum MessageKind : byte
    {
        Node,
        Barrier,
    }

    private readonly struct Message
    {
        public MessageKind Kind { get; private init; }
        public Hash256? Address { get; private init; }
        public TreePath Path { get; private init; }
        public Hash256? Keccak { get; private init; }
        public ulong BlockNumber { get; private init; }
        public ManualResetEventSlim? Barrier { get; private init; }

        public static Message Node(Hash256? address, in TreePath path, Hash256 keccak, ulong blockNumber) =>
            new() { Kind = MessageKind.Node, Address = address, Path = path, Keccak = keccak, BlockNumber = blockNumber };

        public static Message BarrierMessage(ManualResetEventSlim barrier, ulong blockNumber) =>
            new() { Kind = MessageKind.Barrier, Barrier = barrier, BlockNumber = blockNumber };
    }
}
