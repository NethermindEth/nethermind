// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;

namespace Nethermind.State.Flat.History;

/// <summary>
/// Primary (peer/era-fed) concurrent backfill importer: implements the write side of populating or extending a
/// window from any <see cref="IWindowImportSource"/> feed, without depending on a local trie-diff accelerator
/// existing at all. Feeds the exact same raw-bytes row-key layout <see cref="HistoryStore"/> owns
/// (<see cref="HistoryStore.WriteHistoryKey"/>), so imported rows are byte-identical to what forward capture
/// would have produced for the same range.
/// </summary>
/// <remarks>
/// Known gap, not fixable from this subtask: the current <see cref="ChangesetChunkCodec"/> wire shape (owned by
/// 39-1, explicitly documented there as a placeholder pending 39-2) carries account and storage-slot changes only
/// — it has no field for a self-destruct/storage-clear event. Forward capture writes a <c>StorageClears</c> row
/// for every self-destruct that had persisted storage; this importer cannot reconstruct that row because the wire
/// data it receives does not carry it. Rows in <c>AccountHistory</c> and <c>StorageHistory</c> import
/// byte-identical; <c>StorageClears</c> rows do not, for any block containing a self-destruct with prior storage.
/// This needs the codec extended with a destruct/clear signal before that gap closes — reported to 39-1/39-2
/// rather than changed here, since <see cref="ChangesetChunkCodec"/> is explicitly still-evolving seam ownership.
/// </remarks>
public sealed class PeerFedWindowImporter
{
    // A GB/h throughput target belongs to a real EXPB-class benchmark (production-scale hardware, real network
    // feed), not a wall-clock unit test — the project's standing rule against timing tests applies here exactly
    // as it does everywhere else. This constant is the number a future benchmark is measured against.
    public const double TargetThroughputGigabytesPerHour = 20.0;

    private const int BlockBytes = sizeof(ulong);
    private const int ChainBytes = 32;
    private const int ProgressValueLength = 3 * BlockBytes + ChainBytes;

    // A single key per concern, not one key per field: a reader must never observe a torn combination (e.g. a
    // new floor with a stale anchor) from two independent writes to two keys.
    private static ReadOnlySpan<byte> ProgressKey => "history:import:progress"u8; // [targetFloor|targetAnchor|nextBlockToFetch|runningChain]
    private static ReadOnlySpan<byte> ConnectedRangeKey => "history:import:connected"u8; // [floor|anchor]

    private readonly IWindowImportSource _source;
    private readonly IDb _availableBlocks;
    private readonly IDb _accountHistory;
    private readonly IDb _storageHistory;
    private readonly HistoryAvailability _availability;
    private readonly HistoryWindowPruner _pruner;
    private readonly IChangesetHashSource? _hashSource;
    private readonly IImportPeerSink? _peerSink;
    private readonly ValueHash256 _initialChainSeed;
    private readonly WindowImportVerifier _verifier = new();
    private readonly bool _rlpWrapSlots;
    private readonly int _shardCount;
    private readonly ulong _batchBlocks;
    private readonly int _shardBufferBudget;
    private readonly ILogger _logger;

    /// <summary>
    /// <paramref name="hashSource"/>/<paramref name="peerSink"/> are optional: 39-2's real attested-hash channel
    /// and peer pool do not exist yet, so verification is opt-in — omitting them imports and writes rows exactly
    /// as before, with no verification pass at all, which keeps the primary write path testable and usable on its
    /// own per this subtask's own mandate ("build and gate-test the peer-fed path as complete and correct entirely
    /// on its own"). Wiring both turns on per-batch verification.
    /// </summary>
    public PeerFedWindowImporter(
        IWindowImportSource source,
        IColumnsDb<FlatDbColumns> db,
        IColumnsDb<FlatHistoryColumns> history,
        IFlatDbConfig config,
        HistoryWindowPruner pruner,
        ILogManager logManager,
        IChangesetHashSource? hashSource = null,
        IImportPeerSink? peerSink = null,
        ValueHash256 initialChainSeed = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(pruner);
        _source = source;
        _availableBlocks = history.GetColumnDb(FlatHistoryColumns.AvailableBlocks);
        _accountHistory = history.GetColumnDb(FlatHistoryColumns.AccountHistory);
        _storageHistory = history.GetColumnDb(FlatHistoryColumns.StorageHistory);
        _availability = new HistoryAvailability(_availableBlocks);
        _pruner = pruner;
        _hashSource = hashSource;
        _peerSink = peerSink;
        _initialChainSeed = initialChainSeed;
        _logger = logManager.GetClassLogger<PeerFedWindowImporter>();
        _rlpWrapSlots = BasePersistence.ResolveSlotEncoding(db, (ISortedKeyValueStore)db.GetColumnDb(FlatDbColumns.Storage), _logger);
        _shardCount = Math.Max(1, config.HistoryImportShardCount);
        _batchBlocks = Math.Max(1UL, config.HistoryImportBatchBlocks);
        _shardBufferBudget = Math.Max(1, config.HistoryImportShardBufferBudgetEntries);
    }

    /// <summary>The tail-gap rule: a below-anchor read may be served from the fallback-to-live-flat path only when
    /// it falls inside a range that connects contiguously up to the snap-sync anchor. Before that connection
    /// completes, every block in the requested range fails closed — a partially-imported, non-contiguous range
    /// must never let the fallback fire across the gap.</summary>
    public bool IsBelowAnchorServable(ulong block) =>
        TryGetConnectedRange(out ulong floor, out ulong anchor) && block >= floor && block <= anchor;

    public bool TryGetConnectedRange(out ulong floor, out ulong anchor)
    {
        byte[]? value = _availableBlocks.Get(ConnectedRangeKey);
        if (value is not { Length: 2 * BlockBytes })
        {
            floor = 0;
            anchor = 0;
            return false;
        }

        floor = BinaryPrimitives.ReadUInt64BigEndian(value);
        anchor = BinaryPrimitives.ReadUInt64BigEndian(value.AsSpan(BlockBytes));
        return true;
    }

    /// <summary>
    /// Imports <c>[floorBlockInclusive, anchorBlockInclusive]</c> from the configured source, one batch at a
    /// time, resuming from the last durably-completed batch on a prior crash. Below-anchor reads become
    /// servable only once every block in this exact range has actually been received and written — tracked by
    /// the next-expected block number end to end, never inferred from a chunk merely touching the anchor — and
    /// only for this exact (floor, anchor) pair: a persisted cursor from a different target is never reused as
    /// if it covered this one.
    /// </summary>
    public async Task ImportRangeAsync(ulong floorBlockInclusive, ulong anchorBlockInclusive, CancellationToken cancellationToken)
    {
        (ulong expectedBlock, ValueHash256 runningChain) = ResolveResumePoint(floorBlockInclusive, anchorBlockInclusive);
        if (expectedBlock > anchorBlockInclusive)
        {
            PublishConnectedRangeAndLowerFloor(floorBlockInclusive, anchorBlockInclusive);
            return;
        }

        TuneForBulkWrite();
        try
        {
            while (expectedBlock <= anchorBlockInclusive)
            {
                ulong batchEnd = Math.Min(expectedBlock + _batchBlocks - 1, anchorBlockInclusive);
                (List<BlockDigest> digests, ulong lastProcessedBlock, ShardBuffers buffers) =
                    await CollectRangeAsync(_source, expectedBlock, batchEnd, cancellationToken);

                if (digests.Count == 0) break; // the source yielded nothing for this batch: no progress, stop cleanly

                if (_hashSource is not null)
                {
                    (digests, buffers, runningChain) = await VerifyAndRecoverAsync(digests, buffers, runningChain, expectedBlock, cancellationToken);
                }
                else
                {
                    runningChain = WindowImportVerifier.FoldAscending(digests, runningChain);
                }

                using (IDisposable backfillScope = _pruner.BeginBackfill())
                {
                    FlushShards(buffers);
                }

                expectedBlock = lastProcessedBlock + 1;
                PersistProgress(floorBlockInclusive, anchorBlockInclusive, expectedBlock, runningChain);
            }
        }
        finally
        {
            TuneForDefault();
        }

        if (expectedBlock == anchorBlockInclusive + 1)
        {
            PublishConnectedRangeAndLowerFloor(floorBlockInclusive, anchorBlockInclusive);
        }
    }

    /// <summary>Verifies one batch's digests; on a mismatch, bans the current source, refetches the whole batch
    /// from an alternate, and verifies that instead — corrupt data is caught before it ever reaches the write
    /// batch, never written then overwritten. Throws (fail closed) if there is no alternate, or the alternate's
    /// data fails verification too.</summary>
    private async Task<(List<BlockDigest> Digests, ShardBuffers Buffers, ValueHash256 RunningChain)> VerifyAndRecoverAsync(
        List<BlockDigest> digests, ShardBuffers buffers, ValueHash256 runningChainBefore, ulong batchStart, CancellationToken cancellationToken)
    {
        WindowImportVerdict verdict = await _verifier.VerifyAsync(digests, runningChainBefore, batchStart, _hashSource!, cancellationToken);
        if (verdict.Verified)
        {
            return (digests, buffers, WindowImportVerifier.FoldAscending(digests, runningChainBefore));
        }

        if (_logger.IsWarn) _logger.Warn(
            $"Changeset verification failed for batch starting at block {batchStart}, isolated to [{verdict.MismatchRangeStart}, {verdict.MismatchRangeEnd}] - banning the source and retrying from an alternate.");

        if (_peerSink is null || !_peerSink.TryGetAlternateSource(_source, out IWindowImportSource? alternate))
        {
            throw new InvalidOperationException(
                $"Changeset verification failed for batch starting at block {batchStart} and no alternate source is available to recover from.");
        }

        _peerSink.BanSource(_source, $"changeset hash chain mismatch isolated to blocks [{verdict.MismatchRangeStart}, {verdict.MismatchRangeEnd}]");

        ulong batchEnd = digests[^1].Block;
        (List<BlockDigest> altDigests, ulong altLastProcessed, ShardBuffers altBuffers) =
            await CollectRangeAsync(alternate, batchStart, batchEnd, cancellationToken);

        if (altLastProcessed != batchEnd)
        {
            throw new InvalidOperationException(
                $"The alternate source could not supply the full batch [{batchStart}, {batchEnd}] after the primary source was banned.");
        }

        WindowImportVerdict altVerdict = await _verifier.VerifyAsync(altDigests, runningChainBefore, batchStart, _hashSource!, cancellationToken);
        if (!altVerdict.Verified)
        {
            throw new InvalidOperationException(
                $"The alternate source's data for batch [{batchStart}, {batchEnd}] also failed changeset verification.");
        }

        return (altDigests, altBuffers, WindowImportVerifier.FoldAscending(altDigests, runningChainBefore));
    }

    /// <summary>Resolves the block to resume fetching from, and the chain value to resume verification from. A
    /// persisted cursor is honored only when it was recorded against this exact (floor, anchor) target — reusing
    /// it for a different, even overlapping, range would let a call for a genuinely unimported segment
    /// short-circuit as already-done without ever touching the source.</summary>
    private (ulong NextBlock, ValueHash256 RunningChain) ResolveResumePoint(ulong floorBlockInclusive, ulong anchorBlockInclusive)
    {
        if (!TryReadProgress(out ulong targetFloor, out ulong targetAnchor, out ulong cursor, out ValueHash256 chain) ||
            targetFloor != floorBlockInclusive || targetAnchor != anchorBlockInclusive)
        {
            return (floorBlockInclusive, _initialChainSeed);
        }

        return (Math.Max(cursor, floorBlockInclusive), chain);
    }

    /// <summary>Streams <paramref name="source"/> over <c>[fromBlockInclusive, toBlockInclusive]</c>, sharding
    /// rows for the write path and computing each block's digest incrementally (one
    /// <see cref="Nethermind.Core.Crypto.KeccakHash"/> per block, fed as chunks arrive — never a growing byte
    /// list) as it goes, so the network is read exactly once. Stops early — reporting a
    /// <c>lastProcessedBlock</c> below <paramref name="toBlockInclusive"/> — the moment any shard buffer reaches
    /// its budget, so a buffer never grows past it regardless of how large the requested range is.</summary>
    private async Task<(List<BlockDigest> Digests, ulong LastProcessedBlock, ShardBuffers Buffers)> CollectRangeAsync(
        IWindowImportSource source, ulong fromBlockInclusive, ulong toBlockInclusive, CancellationToken cancellationToken)
    {
        ShardBuffers buffers = new(_shardCount, _shardBufferBudget);
        List<BlockDigest> digests = new((int)Math.Min(toBlockInclusive - fromBlockInclusive + 1, (ulong)_batchBlocks));

        ulong expectedBlock = fromBlockInclusive;
        bool processedAny = false;
        ulong lastProcessedBlock = fromBlockInclusive;
        ulong? currentBlock = null;
        bool currentBlockCompleted = true;
        List<ChangesetAccountEntry> currentEntries = [];
        KeccakHash currentBlockHash = KeccakHash.Create();
        uint expectedChunkIndex = 0;
        byte[] digestBuffer = new byte[ChainBytes]; // must survive the await foreach boundary; a stackalloc'd Span cannot

        await foreach (WindowImportChunk chunk in source.GetChangesetsAsync(fromBlockInclusive, toBlockInclusive, cancellationToken))
        {
            if (currentBlock is null || chunk.Block != currentBlock.Value)
            {
                if (!currentBlockCompleted)
                {
                    throw new InvalidOperationException(
                        $"Changeset stream moved to block {chunk.Block} before block {currentBlock} finished (missing the final chunk).");
                }

                if (chunk.Block != expectedBlock)
                {
                    throw new InvalidOperationException(
                        $"Changeset stream has a block gap: expected block {expectedBlock}, got {chunk.Block}. A gap must fail closed, never silently narrow what is later published as connected.");
                }

                currentBlock = chunk.Block;
                currentEntries = [];
                currentBlockHash = KeccakHash.Create();
                expectedChunkIndex = 0;
                currentBlockCompleted = false;
            }

            if (chunk.ChunkIndex != expectedChunkIndex)
            {
                throw new InvalidOperationException(
                    $"Changeset chunk gap for block {chunk.Block}: expected chunk index {expectedChunkIndex}, got {chunk.ChunkIndex}.");
            }

            expectedChunkIndex++;
            currentBlockHash.Update(chunk.Payload.Span);
            currentEntries.AddRange(DecodeChunkPayload(chunk));

            if (!chunk.IsLastChunkForBlock) continue;

            currentBlockCompleted = true;
            DistributeIntoShards(chunk.Block, currentEntries, buffers);

            currentBlockHash.UpdateFinalTo(digestBuffer);
            digests.Add(new BlockDigest(chunk.Block, new ValueHash256(digestBuffer)));

            processedAny = true;
            lastProcessedBlock = chunk.Block;
            expectedBlock = chunk.Block + 1;

            if (chunk.Block == toBlockInclusive || buffers.AnyOverBudget()) break;
        }

        if (!currentBlockCompleted && !cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException($"Changeset stream ended mid-block at block {currentBlock} without a final chunk.");
        }

        return (digests, processedAny ? lastProcessedBlock : fromBlockInclusive, buffers);
    }

    private static List<ChangesetAccountEntry> DecodeChunkPayload(WindowImportChunk chunk)
    {
        try
        {
            return ChangesetChunkCodec.Decode(chunk.Payload.Span);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Codec.Decode throws whatever exception type falls out of misreading attacker-or-corruption-controlled
            // length prefixes (e.g. ArgumentOutOfRangeException) — normalized here so every failure mode from a
            // bad source surfaces as the one exception type this importer's fail-closed contract uses throughout.
            throw new InvalidOperationException($"Malformed changeset payload for block {chunk.Block}.", e);
        }
    }

    private void DistributeIntoShards(ulong block, List<ChangesetAccountEntry> entries, ShardBuffers buffers)
    {
        Span<byte> accountKeyBuffer = stackalloc byte[BaseFlatPersistence.AccountKeyLength];
        Span<byte> storageKeyBuffer = stackalloc byte[BaseFlatPersistence.StorageKeyLength];
        Span<byte> storageValueBuffer = stackalloc byte[BaseFlatPersistence.RlpSlotValueBufferSize];

        foreach (ChangesetAccountEntry entry in entries)
        {
            ValueHash256 addrHash = entry.Address.ToAccountPath;
            ShardBuffer shard = buffers[ShardOf(addrHash, buffers.ShardCount)];

            if (entry.AccountChanged)
            {
                ReadOnlySpan<byte> flatKey = BaseFlatPersistence.EncodeAccountKeyHashed(accountKeyBuffer, addrHash);
                byte[] historyKey = BuildHistoryRowKey(flatKey, block);
                byte[] value = entry.AccountValue.IsEmpty ? [] : entry.AccountValue.ToArray();
                shard.AccountRows.Add((historyKey, value));
            }

            foreach (ChangesetSlotEntry slot in entry.StorageChanges)
            {
                ValueHash256 slotHash = ValueKeccak.Zero;
                StorageTree.ComputeKeyWithLookup(slot.Slot, ref slotHash);
                ReadOnlySpan<byte> flatKey = BaseFlatPersistence.EncodeStorageKeyHashedWithShortPrefix(storageKeyBuffer, addrHash, slotHash);
                byte[] historyKey = BuildHistoryRowKey(flatKey, block);

                int written = slot.Value.IsEmpty
                    ? 0
                    : BaseFlatPersistence.EncodeSlotValue(SlotValue.FromSpanWithoutLeadingZero(slot.Value.Span), _rlpWrapSlots, storageValueBuffer);
                byte[] value = storageValueBuffer[..written].ToArray();
                shard.StorageRows.Add((historyKey, value));
            }
        }
    }

    private void FlushShards(ShardBuffers buffers)
    {
        for (int i = 0; i < buffers.ShardCount; i++)
        {
            ShardBuffer shard = buffers[i];
            WriteSortedRows(_accountHistory, shard.AccountRows);
            WriteSortedRows(_storageHistory, shard.StorageRows);
            shard.Clear();
        }
    }

    private static void WriteSortedRows(IDb column, List<(byte[] Key, byte[] Value)> rows)
    {
        if (rows.Count == 0) return;

        rows.Sort((left, right) => Bytes.BytesComparer.Compare(left.Key, right.Key));
        using IWriteBatch batch = column.StartWriteBatch();
        foreach ((byte[] key, byte[] value) in rows)
        {
            batch.Set(key, value);
        }
    }

    /// <summary>Widens the write buffer / L0 compaction trigger for the duration of the import, then restores the
    /// default profile — the repo's own bulk-import precedent (<c>EraImporter</c>) for exactly this shape of
    /// workload, in place of the manual per-flush <c>CompactRange</c> this importer no longer issues. A no-op on
    /// backends that do not implement <see cref="ITunableDb"/> (e.g. in-memory test doubles).</summary>
    private void TuneForBulkWrite()
    {
        (_accountHistory as ITunableDb)?.Tune(ITunableDb.TuneType.HeavyWrite);
        (_storageHistory as ITunableDb)?.Tune(ITunableDb.TuneType.HeavyWrite);
    }

    private void TuneForDefault()
    {
        (_accountHistory as ITunableDb)?.Tune(ITunableDb.TuneType.Default);
        (_storageHistory as ITunableDb)?.Tune(ITunableDb.TuneType.Default);
    }

    private static int ShardOf(in ValueHash256 addrHash, int shardCount) => addrHash.Bytes[0] * shardCount / 256;

    private static byte[] BuildHistoryRowKey(ReadOnlySpan<byte> flatKey, ulong block)
    {
        byte[] key = new byte[flatKey.Length + BlockBytes];
        HistoryStore.WriteHistoryKey(key, flatKey, block);
        return key;
    }

    private bool TryReadProgress(out ulong targetFloor, out ulong targetAnchor, out ulong cursor, out ValueHash256 chain)
    {
        byte[]? value = _availableBlocks.Get(ProgressKey);
        if (value is not { Length: ProgressValueLength })
        {
            targetFloor = 0;
            targetAnchor = 0;
            cursor = 0;
            chain = default;
            return false;
        }

        targetFloor = BinaryPrimitives.ReadUInt64BigEndian(value);
        targetAnchor = BinaryPrimitives.ReadUInt64BigEndian(value.AsSpan(BlockBytes));
        cursor = BinaryPrimitives.ReadUInt64BigEndian(value.AsSpan(2 * BlockBytes));
        chain = new ValueHash256(value.AsSpan(3 * BlockBytes, ChainBytes));
        return true;
    }

    /// <summary>Durable pointer: the next block to fetch on resume, and the verified chain value to resume
    /// folding from, scoped to the exact (floor, anchor) target they were recorded against. Published after the
    /// batch's writes are already in the column (not before), mirroring
    /// <see cref="HistoryWriter.CaptureUpToCore"/>'s publish-durably ordering — a crash between the writes and
    /// this call simply re-imports the same (idempotent — same key, same value) batch on restart.</summary>
    private void PersistProgress(ulong targetFloor, ulong targetAnchor, ulong nextBlock, ValueHash256 chain)
    {
        Span<byte> value = stackalloc byte[ProgressValueLength];
        BinaryPrimitives.WriteUInt64BigEndian(value, targetFloor);
        BinaryPrimitives.WriteUInt64BigEndian(value[BlockBytes..], targetAnchor);
        BinaryPrimitives.WriteUInt64BigEndian(value[(2 * BlockBytes)..], nextBlock);
        chain.Bytes.CopyTo(value[(3 * BlockBytes)..]);
        _availableBlocks.PutSpan(ProgressKey, value);
        _availableBlocks.SyncWal();
    }

    /// <summary>Publishes the connected range as a single key (never two independent writes a reader could observe
    /// torn), and extends the retention floor down to cover it. The floor extension matters:
    /// <see cref="HistoryWindowPruner"/> computes its own floor from <c>watermark - retention</c> with no notion
    /// that a backfill just widened coverage further into the past, so without this call its very next pass would
    /// delete the rows just imported as "below floor". Only ever lowers — raising it here would wrongly narrow the
    /// servable window for rows the pruner has not actually deleted. Publishing also stamps the windowed format
    /// version (via <see cref="HistoryAvailability.PublishGlobalFloor"/>), which a fresh, never-forward-captured
    /// node (backfill running right after a snap-sync pivot, before any capture batch or genesis seed) would
    /// otherwise never stamp — leaving <see cref="HistoryAvailability.VerifyFormat"/> to treat these reserved keys
    /// as a pre-versioning layout and refuse to start on the very next restart.</summary>
    private void PublishConnectedRangeAndLowerFloor(ulong floor, ulong anchor)
    {
        Span<byte> value = stackalloc byte[2 * BlockBytes];
        BinaryPrimitives.WriteUInt64BigEndian(value, floor);
        BinaryPrimitives.WriteUInt64BigEndian(value[BlockBytes..], anchor);
        _availableBlocks.PutSpan(ConnectedRangeKey, value);

        if (!_availability.TryGetGlobalFloor(out ulong currentFloor) || floor < currentFloor)
        {
            _availability.PublishGlobalFloor(floor);
        }

        _availableBlocks.SyncWal();
    }

    private sealed class ShardBuffer(int budget)
    {
        // Halved, not the full budget, for each of the two lists: IsOverBudget trips on their combined count, so
        // reserving the whole budget per list would over-reserve roughly 2x what is ever reachable.
        public readonly List<(byte[] Key, byte[] Value)> AccountRows = new(Math.Max(1, budget / 2));
        public readonly List<(byte[] Key, byte[] Value)> StorageRows = new(Math.Max(1, budget / 2));

        public bool IsOverBudget => AccountRows.Count + StorageRows.Count >= budget;

        public void Clear()
        {
            AccountRows.Clear();
            StorageRows.Clear();
        }
    }

    private sealed class ShardBuffers
    {
        private readonly ShardBuffer[] _shards;

        public ShardBuffers(int shardCount, int budgetPerShard)
        {
            ShardCount = shardCount;
            _shards = new ShardBuffer[shardCount];
            for (int i = 0; i < shardCount; i++)
            {
                _shards[i] = new ShardBuffer(budgetPerShard);
            }
        }

        public int ShardCount { get; }

        public ShardBuffer this[int index] => _shards[index];

        public bool AnyOverBudget()
        {
            for (int i = 0; i < _shards.Length; i++)
            {
                if (_shards[i].IsOverBudget) return true;
            }

            return false;
        }
    }
}
