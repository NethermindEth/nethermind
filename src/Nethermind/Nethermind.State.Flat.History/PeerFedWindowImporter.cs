// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;

namespace Nethermind.State.Flat.History;

/// <summary>
/// Primary (peer/era-fed) concurrent backfill importer: implements the write side of populating or extending a
/// window from any <see cref="IWindowImportSource"/> feed, without depending on a local trie-diff accelerator
/// existing at all.
/// </summary>
/// <remarks>
/// v3 rows are pre-values (see <see cref="HistoryStoreV3"/>), and the wire (<see cref="ChangesetAccountEntry"/>/
/// <see cref="ChangesetSlotEntry"/>) carries the pre-value for every touch directly — <c>AccountPreValue</c>/
/// <c>PreValue</c> — so no chaining or cross-touch derivation is needed: each touch's row is exactly its own
/// wire-carried pre-value, re-encoded to match the live column's byte shape (RLP-wrapped for storage slots when
/// configured; already-identical bytes for accounts). Known remaining gap, not fixable from here: the wire still
/// carries account and storage-slot changes only, with no self-destruct signal, so this importer cannot
/// reconstruct a <c>StorageClears</c> row for a self-destruct within the imported range.
/// </remarks>
public sealed class PeerFedWindowImporter
{
    /// <summary>The stated import throughput target once the row-key/value gap above is closed and this importer
    /// writes real data — a GB/h number belongs to a real EXPB-class benchmark (production-scale hardware, a real
    /// network feed), not a wall-clock unit test, so this constant records the number such a benchmark is
    /// measured against rather than being asserted anywhere in this project's test suite.</summary>
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
    private readonly IDb _accountHistoryColumn;
    private readonly IDb _storageHistoryColumn;
    private readonly HistoryAvailability _availability;
    private readonly HistoryRowFormat _rowFormat;
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
    /// <paramref name="hashSource"/>/<paramref name="peerSink"/> are optional: the real attested-hash channel and
    /// peer pool do not exist yet, so verification is opt-in — omitting them imports without a verification
    /// pass. <paramref name="availability"/>/<paramref name="rowFormat"/> are the shared, DI-owned singletons
    /// (see <see cref="Nethermind.Init.Modules.FlatHistoryModule"/>) — this type must never construct its own
    /// <see cref="HistoryAvailability"/>, since a floor lowered here has to be observed immediately by every other
    /// holder (the pruner in particular).
    /// </summary>
    public PeerFedWindowImporter(
        IWindowImportSource source,
        IColumnsDb<FlatDbColumns> db,
        IColumnsDb<FlatHistoryColumns> history,
        IFlatDbConfig config,
        HistoryWindowPruner pruner,
        HistoryAvailability availability,
        HistoryRowFormat rowFormat,
        ILogManager logManager,
        IChangesetHashSource? hashSource = null,
        IImportPeerSink? peerSink = null,
        ValueHash256 initialChainSeed = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(pruner);
        ArgumentNullException.ThrowIfNull(availability);
        ArgumentNullException.ThrowIfNull(rowFormat);
        _source = source;
        _availableBlocks = history.GetColumnDb(FlatHistoryColumns.AvailableBlocks);
        _accountHistoryColumn = history.GetColumnDb(FlatHistoryColumns.AccountHistory);
        _storageHistoryColumn = history.GetColumnDb(FlatHistoryColumns.StorageHistory);
        _availability = availability;
        _rowFormat = rowFormat;
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
    /// time, resuming from the last durably-completed batch on a prior crash. Refuses outright on an unwindowed
    /// (v2) database: backfilling a window is meaningless for a node configured to keep everything from genesis
    /// unbounded, and letting the call proceed would silently stamp the windowed format as a side effect of a
    /// call whose only job is populating a window this node's own configuration says it does not want.
    /// </summary>
    public Task ImportRangeAsync(ulong floorBlockInclusive, ulong anchorBlockInclusive, CancellationToken cancellationToken)
    {
        if (!_rowFormat.IsV3)
        {
            throw new InvalidConfigurationException(
                "Backfill import requires a windowed (v3) flatHistory database (HistoryRetentionBlocks > 0). " +
                "This node's flatHistory database is unwindowed (v2); refusing rather than silently stamping it " +
                "to the windowed format as a side effect of importing. Configure HistoryRetentionBlocks to enable " +
                "windowing before running a backfill import.", -1);
        }

        return ImportRangeCoreAsync(floorBlockInclusive, anchorBlockInclusive, cancellationToken);
    }

    /// <summary>The real pipeline, gated behind <see cref="ImportRangeAsync"/>'s v2 refusal. Kept internal and
    /// separately callable so tests can exercise batching, verification, floor-lowering, and the backfill gate
    /// directly, independent of that outer check.</summary>
    internal async Task ImportRangeCoreAsync(ulong floorBlockInclusive, ulong anchorBlockInclusive, CancellationToken cancellationToken)
    {
        (ulong expectedBlock, ValueHash256 runningChain) = ResolveResumePoint(floorBlockInclusive, anchorBlockInclusive);
        if (expectedBlock > anchorBlockInclusive)
        {
            await PublishConnectedRangeAndLowerFloorAsync(floorBlockInclusive, anchorBlockInclusive, cancellationToken);
            return;
        }

        TuneForBulkWrite();
        try
        {
            while (expectedBlock <= anchorBlockInclusive)
            {
                ulong batchEnd = Math.Min(expectedBlock + _batchBlocks - 1, anchorBlockInclusive);
                CollectedBatch batch = await CollectRangeAsync(_source, expectedBlock, batchEnd, cancellationToken);
                if (batch.Digests.Count == 0) break; // the source yielded nothing for this batch: no progress, stop cleanly

                if (_hashSource is not null)
                {
                    (batch, runningChain) = await VerifyAndRecoverAsync(batch, runningChain, expectedBlock, cancellationToken);
                }
                else
                {
                    runningChain = WindowImportVerifier.FoldAscending(batch.Digests, runningChain);
                }

                ShardBuffers buffers = ShardTouches(batch.Touches, _shardCount, _shardBufferBudget);

                using (await _pruner.BeginBackfillAsync(cancellationToken))
                {
                    FlushShards(buffers);
                }

                expectedBlock = batch.LastProcessedBlock + 1;
                PersistProgress(floorBlockInclusive, anchorBlockInclusive, expectedBlock, runningChain);
            }
        }
        finally
        {
            TuneForDefault();
        }

        if (expectedBlock == anchorBlockInclusive + 1)
        {
            await PublishConnectedRangeAndLowerFloorAsync(floorBlockInclusive, anchorBlockInclusive, cancellationToken);
        }
    }

    /// <summary>Verifies one batch's digests; on a mismatch, bans the current source, refetches the whole batch
    /// from an alternate, and verifies that instead — corrupt data is caught before it ever reaches the write
    /// batch, never written then overwritten. Throws (fail closed) if there is no alternate, or the alternate's
    /// data fails verification too.</summary>
    private async Task<(CollectedBatch Batch, ValueHash256 RunningChain)> VerifyAndRecoverAsync(
        CollectedBatch batch, ValueHash256 runningChainBefore, ulong batchStart, CancellationToken cancellationToken)
    {
        WindowImportVerdict verdict = await _verifier.VerifyAsync(batch.Digests, runningChainBefore, batchStart, _hashSource!, cancellationToken);
        if (verdict.Verified)
        {
            return (batch, WindowImportVerifier.FoldAscending(batch.Digests, runningChainBefore));
        }

        if (_logger.IsWarn) _logger.Warn(
            $"Changeset verification failed for batch starting at block {batchStart}, isolated to [{verdict.MismatchRangeStart}, {verdict.MismatchRangeEnd}] - banning the source and retrying from an alternate.");

        if (_peerSink is null || !_peerSink.TryGetAlternateSource(_source, out IWindowImportSource? alternate))
        {
            throw new InvalidOperationException(
                $"Changeset verification failed for batch starting at block {batchStart} and no alternate source is available to recover from.");
        }

        _peerSink.BanSource(_source, $"changeset hash chain mismatch isolated to blocks [{verdict.MismatchRangeStart}, {verdict.MismatchRangeEnd}]");

        ulong batchEnd = batch.Digests[^1].Block;
        CollectedBatch altBatch = await CollectRangeAsync(alternate, batchStart, batchEnd, cancellationToken);
        if (altBatch.LastProcessedBlock != batchEnd)
        {
            throw new InvalidOperationException(
                $"The alternate source could not supply the full batch [{batchStart}, {batchEnd}] after the primary source was banned.");
        }

        WindowImportVerdict altVerdict = await _verifier.VerifyAsync(altBatch.Digests, runningChainBefore, batchStart, _hashSource!, cancellationToken);
        if (!altVerdict.Verified)
        {
            throw new InvalidOperationException(
                $"The alternate source's data for batch [{batchStart}, {batchEnd}] also failed changeset verification.");
        }

        return (altBatch, WindowImportVerifier.FoldAscending(altBatch.Digests, runningChainBefore));
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

    internal readonly record struct RawTouch(byte[] FlatKey, ulong Block, byte[] PreValue, bool IsAccount);

    internal readonly record struct CollectedBatch(List<RawTouch> Touches, List<BlockDigest> Digests, ulong LastProcessedBlock);

    /// <summary>Streams <paramref name="source"/> over <c>[fromBlockInclusive, toBlockInclusive]</c>, decoding and
    /// validating the wire stream (block-gap, chunk-gap, mid-block-end, and malformed-payload guards) and
    /// computing each block's digest incrementally (one <see cref="KeccakHash"/> per block, fed as chunks
    /// arrive — never a growing byte list) as it goes, so the network is read exactly once. Internal (not
    /// private) so the decode/validate/digest mechanism is directly testable on its own.</summary>
    internal async Task<CollectedBatch> CollectRangeAsync(IWindowImportSource source, ulong fromBlockInclusive, ulong toBlockInclusive, CancellationToken cancellationToken)
    {
        List<RawTouch> touches = [];
        List<BlockDigest> digests = new((int)Math.Min(toBlockInclusive - fromBlockInclusive + 1, (ulong)_batchBlocks));
        BlockStreamCursor cursor = new(fromBlockInclusive);

        await foreach (WindowImportChunk chunk in source.GetChangesetsAsync(fromBlockInclusive, toBlockInclusive, cancellationToken))
        {
            cursor.AdvanceTo(chunk);
            cursor.Hash.Update(chunk.Payload.Span);
            cursor.Entries.AddRange(DecodeChunkPayload(chunk));

            if (!chunk.IsLastChunkForBlock) continue;

            AppendTouches(chunk.Block, cursor.Entries, touches);
            digests.Add(cursor.FinalizeDigest(chunk.Block));

            if (chunk.Block == toBlockInclusive || TooManyTouchesBuffered(touches)) break;
        }

        cursor.ThrowIfIncomplete(cancellationToken);
        return new CollectedBatch(touches, digests, cursor.LastCompletedBlock ?? fromBlockInclusive);
    }

    /// <summary>Cheap early-stop guard so an unexpectedly wide range cannot grow the touch list without bound —
    /// mirrors the same budget the shard buffers are sized against, checked here instead of after sharding since
    /// sharding now happens once, after the whole batch is collected.</summary>
    private bool TooManyTouchesBuffered(List<RawTouch> touches) => touches.Count >= _shardBufferBudget * _shardCount;

    /// <summary>Per-block streaming state for <see cref="CollectRangeAsync"/>: tracks which block is currently
    /// being assembled, enforces the block-gap/chunk-gap/mid-block-end guards, and folds the block's raw payload
    /// bytes into a running digest as chunks arrive.</summary>
    private sealed class BlockStreamCursor(ulong fromBlockInclusive)
    {
        private ulong _expectedBlock = fromBlockInclusive;
        private ulong? _currentBlock;
        private bool _currentBlockCompleted = true;
        private uint _expectedChunkIndex;
        private readonly byte[] _digestBuffer = new byte[ChainBytes]; // must survive the await foreach boundary; a stackalloc'd Span cannot

        public List<ChangesetAccountEntry> Entries { get; private set; } = [];
        public KeccakHash Hash { get; private set; } = KeccakHash.Create();
        public ulong? LastCompletedBlock { get; private set; }

        public void AdvanceTo(WindowImportChunk chunk)
        {
            if (_currentBlock is not null && chunk.Block == _currentBlock.Value)
            {
                if (chunk.ChunkIndex != _expectedChunkIndex)
                {
                    throw new InvalidOperationException(
                        $"Changeset chunk gap for block {chunk.Block}: expected chunk index {_expectedChunkIndex}, got {chunk.ChunkIndex}.");
                }

                _expectedChunkIndex++;
                return;
            }

            if (!_currentBlockCompleted)
            {
                throw new InvalidOperationException(
                    $"Changeset stream moved to block {chunk.Block} before block {_currentBlock} finished (missing the final chunk).");
            }

            if (chunk.Block != _expectedBlock)
            {
                throw new InvalidOperationException(
                    $"Changeset stream has a block gap: expected block {_expectedBlock}, got {chunk.Block}. A gap must fail closed, never silently narrow what is later published as connected.");
            }

            if (chunk.ChunkIndex != 0)
            {
                throw new InvalidOperationException(
                    $"Changeset chunk gap for block {chunk.Block}: expected chunk index 0, got {chunk.ChunkIndex}.");
            }

            _currentBlock = chunk.Block;
            Entries = [];
            Hash = KeccakHash.Create();
            _expectedChunkIndex = 1;
            _currentBlockCompleted = false;
        }

        public BlockDigest FinalizeDigest(ulong block)
        {
            _currentBlockCompleted = true;
            LastCompletedBlock = block;
            _expectedBlock = block + 1;
            Hash.UpdateFinalTo(_digestBuffer);
            return new BlockDigest(block, new ValueHash256(_digestBuffer));
        }

        public void ThrowIfIncomplete(CancellationToken cancellationToken)
        {
            if (!_currentBlockCompleted && !cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException($"Changeset stream ended mid-block at block {_currentBlock} without a final chunk.");
            }
        }
    }

    /// <summary>Extracts each touch's row directly from the wire's own pre-value fields — <c>AccountPreValue</c>
    /// is already the exact bytes the <c>AccountHistory</c> column stores (the same <c>AccountDecoder.Slim</c>
    /// RLP forward capture both writes and puts on the wire), but a storage slot's wire pre-value is the raw,
    /// leading-zeros-stripped bytes and must be re-encoded (RLP-wrapped when configured) to match what
    /// <c>StorageHistory</c> actually stores — mirroring <see cref="HistoryWriter.RecordStorageV3"/> exactly, so
    /// an imported row is byte-identical to what forward capture would have written for the same change.</summary>
    private void AppendTouches(ulong block, List<ChangesetAccountEntry> entries, List<RawTouch> touches)
    {
        Span<byte> accountKeyBuffer = stackalloc byte[HistoryKeyLayout.AccountKeyLength];
        Span<byte> storageKeyBuffer = stackalloc byte[BaseFlatPersistence.StorageKeyLength];
        Span<byte> storageValueBuffer = stackalloc byte[BaseFlatPersistence.RlpSlotValueBufferSize];

        foreach (ChangesetAccountEntry entry in entries)
        {
            ValueHash256 addrHash = entry.Address.ToAccountPath;

            if (entry.AccountChanged)
            {
                byte[] flatKey = HistoryKeyLayout.EncodeAccountKey(accountKeyBuffer, addrHash).ToArray();
                byte[] preValue = entry.AccountPreValue.IsEmpty ? [] : entry.AccountPreValue.ToArray();
                touches.Add(new RawTouch(flatKey, block, preValue, IsAccount: true));
            }

            foreach (ChangesetSlotEntry slot in entry.StorageChanges)
            {
                ValueHash256 slotHash = ValueKeccak.Zero;
                StorageTree.ComputeKeyWithLookup(slot.Slot, ref slotHash);
                byte[] flatKey = BaseFlatPersistence.EncodeStorageKeyHashedWithShortPrefix(storageKeyBuffer, addrHash, slotHash).ToArray();

                int written = slot.PreValue.IsEmpty
                    ? 0
                    : BaseFlatPersistence.EncodeSlotValue(SlotValue.FromSpanWithoutLeadingZero(slot.PreValue.Span), _rlpWrapSlots, storageValueBuffer);
                byte[] preValue = storageValueBuffer[..written].ToArray();
                touches.Add(new RawTouch(flatKey, block, preValue, IsAccount: false));
            }
        }
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

    /// <summary>Shards every touch's already-resolved v3 row (see <see cref="AppendTouches"/> — each touch's
    /// pre-value comes straight from the wire, no cross-touch derivation needed) by address-hash prefix, ready
    /// for a sorted, per-shard batch write.</summary>
    private static ShardBuffers ShardTouches(List<RawTouch> touches, int shardCount, int budgetPerShard)
    {
        ShardBuffers buffers = new(shardCount, budgetPerShard);
        foreach (RawTouch touch in touches)
        {
            ShardBuffer shard = buffers[ShardOfFlatKey(touch.FlatKey, shardCount)];
            byte[] historyKey = BuildV3RowKey(touch.FlatKey, touch.Block);
            (touch.IsAccount ? shard.AccountRows : shard.StorageRows).Add((historyKey, touch.PreValue));
        }

        return buffers;
    }

    private void FlushShards(ShardBuffers buffers)
    {
        for (int i = 0; i < buffers.ShardCount; i++)
        {
            ShardBuffer shard = buffers[i];
            WriteSortedRows(_accountHistoryColumn, shard.AccountRows);
            WriteSortedRows(_storageHistoryColumn, shard.StorageRows);
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
    /// workload. A no-op on backends that do not implement <see cref="ITunableDb"/> (e.g. in-memory test doubles).</summary>
    private void TuneForBulkWrite()
    {
        (_accountHistoryColumn as ITunableDb)?.Tune(ITunableDb.TuneType.HeavyWrite);
        (_storageHistoryColumn as ITunableDb)?.Tune(ITunableDb.TuneType.HeavyWrite);
    }

    private void TuneForDefault()
    {
        (_accountHistoryColumn as ITunableDb)?.Tune(ITunableDb.TuneType.Default);
        (_storageHistoryColumn as ITunableDb)?.Tune(ITunableDb.TuneType.Default);
    }

    private static int ShardOfFlatKey(byte[] flatKey, int shardCount) => flatKey[0] * shardCount / 256;

    /// <summary>v3's row-key layout (<c>[flatKey | block BE]</c>, ascending — the complement of v2's
    /// <see cref="HistoryStore.WriteHistoryKey"/>). <see cref="HistoryStoreV3"/> owns the same encoding internally
    /// but does not expose it for external key-building; reimplemented here rather than widening its visibility,
    /// since unlike v2's already-shared helper this layout is small, fixed, and fully specified by
    /// <see cref="HistoryStoreV3"/>'s own public remarks with no ambiguity to duplicate incorrectly.</summary>
    private static byte[] BuildV3RowKey(byte[] flatKey, ulong block)
    {
        byte[] key = new byte[flatKey.Length + BlockBytes];
        flatKey.CopyTo(key, 0);
        BinaryPrimitives.WriteUInt64BigEndian(key.AsSpan(flatKey.Length), block);
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
    /// torn), and extends the retention floor down to cover it via <see cref="HistoryAvailability.TryLowerGlobalFloor"/>
    /// — never a raw key write, and never <see cref="HistoryAvailability.PublishGlobalFloor"/> (unconditional,
    /// reserved for the pruner's own initial seed): the CAS pair with the pruner's
    /// <see cref="HistoryAvailability.TryRaiseGlobalFloor"/> is what stops a concurrent prune pass and this call
    /// from clobbering each other's write to the same shared instance. <see cref="HistoryWindowPruner"/> already
    /// consults <see cref="HistoryAvailability.TryGetConnectedRange"/> before raising the floor past this range's
    /// bottom, so the two sides only need to agree on which method owns which direction.</summary>
    private async Task PublishConnectedRangeAndLowerFloorAsync(ulong floor, ulong anchor, CancellationToken cancellationToken)
    {
        Span<byte> value = stackalloc byte[2 * BlockBytes];
        BinaryPrimitives.WriteUInt64BigEndian(value, floor);
        BinaryPrimitives.WriteUInt64BigEndian(value[BlockBytes..], anchor);
        _availableBlocks.PutSpan(ConnectedRangeKey, value);
        _availableBlocks.SyncWal();

        using (await _pruner.BeginBackfillAsync(cancellationToken))
        {
            _availability.TryLowerGlobalFloor(floor);
        }
    }

    private sealed class ShardBuffer(int budget)
    {
        // Halved, not the full budget, for each of the two lists: IsOverBudget trips on their combined count, so
        // reserving the whole budget per list would over-reserve roughly 2x what is ever reachable.
        public readonly List<(byte[] Key, byte[] Value)> AccountRows = new(Math.Max(1, budget / 2));
        public readonly List<(byte[] Key, byte[] Value)> StorageRows = new(Math.Max(1, budget / 2));

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
    }
}
