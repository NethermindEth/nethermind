// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;
using Autofac.Features.AttributeFilters;
using Nethermind.Api.Steps;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Init.Steps;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Pbt;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Pbt.Persistence;
using FlatPersistence = Nethermind.State.Flat.Persistence.IPersistence;
using FlatStateId = Nethermind.State.Flat.StateId;

namespace Nethermind.State.Pbt.Steps;

/// <summary>
/// A one-shot step that rebuilds the PBT state from an existing preimage-flat state database. In
/// preimage-flat layout the flat entries are keyed by the original address/slot, so iterating them
/// yields exactly the account/slot preimages the stem tree needs; a hashed source could not be used.
/// The step feeds decoded leaves to <see cref="PbtRebuilder"/>, then exits the process (mirroring
/// <c>ImportFlatDb</c>).
/// </summary>
/// <remarks>
/// The import runs in two phases, and the leaf blob columns are what carries state between them: first
/// the source is laid out as <see cref="PbtLeafFormat.LeavesOnly"/> blobs, in parallel over ranges of
/// the source address space; then those blobs are scanned back and folded into the tree.
/// <para>
/// The first phase is what re-sorts the data. A preimage-flat source is keyed by raw address, while a
/// stem is derived from its hash, so the two orders are unrelated — but the leaf columns are keyed by
/// stem, so writing the blobs is itself the sort, and phase two reads them back in exactly the order
/// the rebuild wants. Laying them out costs no hash at all: the leaves-only layout stores no internal
/// node, and the fold that phase two runs is what merkelizes them.
/// </para>
/// <para>
/// The phases cannot overlap: phase one partitions the source by address, which scatters across the
/// whole stem space, so no range of phase two's key space is complete until all of phase one is.
/// </para>
/// </remarks>
[RunnerStepDependencies(
    dependencies: [typeof(InitializeBlockTree)],
    dependents: [typeof(InitializeBlockchain)]
)]
public class ImportPbtFromPreimageFlat(
    FlatPersistence flatSource,
    [KeyFilter(DbNames.Code)] IDb codeDb,
    IColumnsDb<PbtColumns> pbtDb,
    PbtRebuilder rebuilder,
    IPbtPersistence pbtPersistence,
    IPbtConfig config,
    IProcessExitSource exitSource,
    ILogManager logManager
) : IStep
{
    private const int AddressLength = 20;

    /// <summary>Entries per chunk on the leaf channel; chunking amortizes the channel's per-write cost over the import's billions of leaves.</summary>
    private const int ChunkSize = 2_048;

    /// <summary>Chunks in flight on the leaf channel (~128k leaves with <see cref="ChunkSize"/>).</summary>
    private const int EntryChunkCapacity = 64;

    /// <summary>Account-key ranges per copy worker. Ranges are claimed one at a time, so several per worker keep them busy to the end despite uneven storage sizes.</summary>
    private const int PartitionsPerWorker = 16;

    /// <summary>Distinct values of the two account-key bytes the copy ranges are cut on, bounding the range count.</summary>
    private const int PartitionPrefixSpace = 1 << 16;

    /// <summary>Entries a worker copies before publishing them to the shared counters, so progress moves within a range that takes minutes without an interlocked add per entry.</summary>
    private const int ProgressPublishInterval = 100_000;

    private static readonly TimeSpan CopyLogInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Leaves a phase-two zone scan reads through one leaf-column view before closing it. Bounds how
    /// long a single RocksDB superversion stays pinned — not correctness, which the ascending
    /// scan-ahead-of-fold invariant holds at any chunking (see <see cref="EmitZone"/>).
    /// </summary>
    /// <remarks>
    /// One channel's worth of leaves: the buffer this fills is of the same order as what the entry
    /// channel already holds in flight. A stem-count bound could not be buffered — one stem holds up to
    /// <see cref="PbtKeyDerivation.StemSubtreeWidth"/> leaves.
    /// </remarks>
    internal int ViewLeafChunk { get; init; } = EntryChunkCapacity * ChunkSize;

    /// <summary>
    /// Keys <see cref="ClearInterruptedAttempt"/> deletes through one column view and one write batch
    /// before reopening both. Bounds the same pin <see cref="ViewStemChunk"/> does, on the sweep that
    /// discards a previous run's debris.
    /// </summary>
    internal int ClearKeyChunk { get; init; } = 10_000;

    private readonly ILogger _logger = logManager.GetClassLogger<ImportPbtFromPreimageFlat>();

    public async Task Execute(CancellationToken cancellationToken)
    {
        using (IPbtPersistence.IReader pbtReader = pbtPersistence.CreateReader())
        {
            if (pbtReader.CurrentState != StateId.PreGenesis)
            {
                if (_logger.IsInfo) _logger.Info($"PBT state already populated ({pbtReader.CurrentState}); skipping preimage-flat import.");
                return;
            }
        }

        FlatStateId sourceState;
        // scoped to the two answers it is opened for: a reader pins a snapshot of every source column,
        // and the import that follows runs for hours
        using (FlatPersistence.IPersistenceReader reader = flatSource.CreateReader())
        {
            if (!reader.IsPreimageMode)
            {
                if (_logger.IsError) _logger.Error("Source flat database is not in preimage mode; addresses and slots cannot be recovered to build PBT. Aborting.");
                exitSource.Exit(1);
                return;
            }

            sourceState = reader.CurrentState;
        }

        if (sourceState == FlatStateId.PreGenesis)
        {
            if (_logger.IsInfo) _logger.Info("Source flat database is empty; nothing to import.");
            return;
        }

        int workerCount = config.ImportStorageReadConcurrency > 0 ? config.ImportStorageReadConcurrency : Environment.ProcessorCount;
        if (_logger.IsInfo) _logger.Info($"Rebuilding PBT state from preimage-flat database at {sourceState} with {workerCount} source reader(s)");

        try
        {
            ClearInterruptedAttempt();
            await CopyFlatColumns(workerCount, cancellationToken);

            // the source is keyed by the header's root, which is also how the rest of the node
            // addresses the state; the tree root the fold produces is recorded beside it
            await DeriveAndFold(new StateId(sourceState.BlockNumber, sourceState.StateRoot), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (_logger.IsInfo) _logger.Info("PBT import cancelled.");
            exitSource.Exit(1);
            return;
        }

        exitSource.Exit(0);
    }

    /// <remarks>
    /// A pre-genesis state pointer means no state was ever committed, but an interrupted run can still
    /// have written leaf blobs and trie nodes, and those are not inert:
    /// <see cref="TrieUpdater"/> reads the stored root group before it consults the root hash it was
    /// handed, so a rebuild starting from an empty root folds against the stale tree and settles on the
    /// wrong root. Metadata is left alone — it carries the layout version, and its state pointer is
    /// pre-genesis already.
    /// <para>
    /// A column is swept in <see cref="ClearKeyChunk"/>-key ranges, each read through a view of its own
    /// that is closed before the chunk's deletes are committed. The deletes land in the very column the
    /// view scans, so one view held across the whole sweep would pin a RocksDB superversion for every
    /// version they replace — the sweep's own tombstones included — and process memory would climb with
    /// the size of the database being discarded.
    /// </para>
    /// </remarks>
    private void ClearInterruptedAttempt()
    {
        long cleared = 0;
        byte[] pastEnd = PastEveryKey();

        foreach (PbtColumns column in Enum.GetValues<PbtColumns>())
        {
            if (column == PbtColumns.Metadata) continue;

            ISortedKeyValueStore store = (ISortedKeyValueStore)pbtDb.GetColumnDb(column);
            byte[] cursor = [];
            byte[]? resumeFrom;
            do
            {
                resumeFrom = null;
                using (IColumnsWriteBatch<PbtColumns> batch = pbtDb.StartWriteBatch())
                {
                    IWriteBatch columnBatch = batch.GetColumnBatch(column);
                    using ISortedView view = store.GetViewBetween(cursor, pastEnd);

                    int read = 0;
                    while (read < ClearKeyChunk && view.MoveNext())
                    {
                        columnBatch.Remove(view.CurrentKey);
                        read++;
                    }

                    cleared += read;

                    // the loop stopped on the count rather than on MoveNext, so the view is still on the
                    // last key deleted: resume just past it once this chunk's deletes are committed
                    if (read == ClearKeyChunk) resumeFrom = AfterKey(view.CurrentKey);
                }

                if (resumeFrom is not null) cursor = resumeFrom;
            }
            while (resumeFrom is not null);
        }

        if (cleared > 0 && _logger.IsInfo) _logger.Info($"Discarded {cleared:N0} entries left by an interrupted PBT import.");
    }

    /// <summary>
    /// Phase one: lays the source's accounts and slots out as leaves-only blobs in the leaf columns,
    /// keyed by the stem each belongs to.
    /// </summary>
    /// <remarks>
    /// Each worker owns a source reader, a PBT write batch, its blob scratch and the range it is
    /// currently on, so nothing is shared but the counters. Ranges are handed out on demand rather than
    /// assigned up front because storage sizes are wildly uneven. Every batch is written
    /// <see cref="StateId.PreGenesis"/> → <see cref="StateId.PreGenesis"/> with
    /// <see cref="WriteFlags.DisableWAL"/>: the persisted-state pointer stays pre-genesis until the
    /// rebuild completes, so a crash mid-import leaves a state that the next run simply overwrites —
    /// a stem's blob is a deterministic function of the source, so re-laying it is idempotent.
    /// </remarks>
    private async Task CopyFlatColumns(int workerCount, CancellationToken cancellationToken)
    {
        Stopwatch copying = Stopwatch.StartNew();
        int partitionCount = Math.Min(workerCount * PartitionsPerWorker, PartitionPrefixSpace);

        int nextPartition = -1, donePartitions = 0;
        long accounts = 0, slots = 0;

        void CopyPartitions()
        {
            using LeafBlobWriter leaves = new();

            int partition;
            while ((partition = Interlocked.Increment(ref nextPartition)) < partitionCount)
            {
                (ValueHash256 start, ValueHash256 end) = PartitionBounds(partition, partitionCount);

                // A reader and a batch per range rather than per worker: the reader pins a snapshot of
                // every source column and the batch bounds how much is buffered, neither of which should
                // outlive one range of a copy that runs for hours. The persisted-state pointer the batch
                // rewrites is the same pre-genesis value either way.
                using (FlatPersistence.IPersistenceReader reader = flatSource.CreateReader())
                using (IPbtPersistence.IWriteBatch batch = pbtPersistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, default, WriteFlags.DisableWAL))
                {
                    CopyAccounts(reader, batch, leaves, start, end, ref accounts, ref slots, cancellationToken);
                }

                Interlocked.Increment(ref donePartitions);
            }
        }

        // ProgressLogger is neither thread-safe nor self-throttling, so one ticker owns it and samples
        // the counters the workers publish, rather than the workers logging as they finish a range
        async Task LogCopyProgress(CancellationToken loggingToken)
        {
            long loggedAccounts = 0, loggedSlots = 0;
            double accountsPerSec = 0, slotsPerSec = 0;
            Stopwatch sinceLog = Stopwatch.StartNew();

            // Ranges are equal slices of the account key space and addresses are uniform over it, so
            // the fraction of ranges finished is a fair completion estimate. CurrentValue is driven by
            // the entry count instead, only so it moves every tick: the logger drops a repeated line
            // unless CurrentValue changes, which would stall the log through a long-running range.
            ProgressLogger progress = new("PBT import flat copy", logManager);
            progress.SetFormat(_ =>
            {
                float percentage = Math.Clamp(Volatile.Read(ref donePartitions) / (float)partitionCount, 0, 1);
                return $"PBT import flat copy {percentage.ToString("P2", CultureInfo.InvariantCulture),8} {Progress.GetMeter(percentage, 1)} | " +
                    $"{Interlocked.Read(ref accounts),13:N0} acc ({accountsPerSec,8:N0}/s) | {Interlocked.Read(ref slots),15:N0} slot ({slotsPerSec,8:N0}/s)";
            });
            progress.Reset(0, 0);

            using PeriodicTimer timer = new(CopyLogInterval);
            while (await timer.WaitForNextTickAsync(loggingToken))
            {
                long currentAccounts = Interlocked.Read(ref accounts), currentSlots = Interlocked.Read(ref slots);
                double secs = sinceLog.Elapsed.TotalSeconds;
                if (secs > 0)
                {
                    accountsPerSec = (currentAccounts - loggedAccounts) / secs;
                    slotsPerSec = (currentSlots - loggedSlots) / secs;
                }
                (loggedAccounts, loggedSlots) = (currentAccounts, currentSlots);
                sinceLog.Restart();

                progress.Update((ulong)(currentAccounts + currentSlots));
                progress.LogProgress();
            }
        }

        using CancellationTokenSource loggingCts = new();
        Task logging = Task.Run(async () =>
        {
            try { await LogCopyProgress(loggingCts.Token); }
            catch (OperationCanceledException) { /* the copy finished */ }
        }, CancellationToken.None);

        Task[] workers = new Task[workerCount];
        for (int i = 0; i < workerCount; i++)
        {
            workers[i] = Task.Run(CopyPartitions, cancellationToken);
        }

        try
        {
            await Task.WhenAll(workers);
        }
        finally
        {
            await loggingCts.CancelAsync();
            await logging;
        }

        // the batches skipped the WAL; make them durable before phase two reads them back
        pbtDb.Flush();
        if (_logger.IsInfo) _logger.Info($"PBT import copied {accounts:N0} accounts and {slots:N0} slots in {copying.Elapsed:hh\\:mm\\:ss}.");
    }

    /// <summary>
    /// Writes each account's header stem blob — its <c>BASIC_DATA</c>, <c>CODE_HASH</c>, its first 64
    /// storage slots and its header code chunks, all of which share that stem — plus the
    /// content-addressed overflow chunks of any code too long to fit it.
    /// </summary>
    /// <remarks>
    /// The slots are taken first so that the header stem is written once, complete. That is only
    /// possible because the source hands an account's slots over with the account itself; a sweep of the
    /// storage column would interleave every account sharing a key prefix and no stem would be complete
    /// until the sweep had passed it.
    /// </remarks>
    private void CopyAccounts(
        FlatPersistence.IPersistenceReader reader,
        IPbtPersistence.IWriteBatch batch,
        LeafBlobWriter leaves,
        ValueHash256 start,
        ValueHash256 end,
        ref long accounts,
        ref long slots,
        CancellationToken cancellationToken)
    {
        long pendingAccounts = 0;
        // BASIC_DATA and CODE_HASH sit on adjacent sub-indices, so they go in as one run
        Span<byte> basicDataAndCodeHash = stackalloc byte[2 * ValueHash256.MemorySize];
        using FlatPersistence.IFlatIterator accountIterator = reader.CreateAccountIterator(start, end);
        while (accountIterator.MoveNext())
        {
            cancellationToken.ThrowIfCancellationRequested();

            // preimage mode: the flat key holds the raw address in its first 20 bytes
            ValueHash256 accountKey = accountIterator.CurrentKey;
            Address address = new(accountKey.Bytes[..AddressLength]);

            Account account = DecodeAccount(accountIterator.CurrentValue);
            byte[]? code = account.HasCode
                ? codeDb.Get(account.CodeHash.Bytes) ?? throw new InvalidDataException($"Missing bytecode for {address} (code hash {account.CodeHash}) in the code database.")
                : null;

            leaves.BeginAccount();
            if (account.HasStorage) CopySlots(reader, batch, leaves, accountKey, address, ref slots, cancellationToken);

            PbtKeyDerivation.PackBasicData(basicDataAndCodeHash[..ValueHash256.MemorySize], code is null ? 0u : (uint)code.Length, account.Nonce, account.Balance);
            account.CodeHash.Bytes.CopyTo(basicDataAndCodeHash[ValueHash256.MemorySize..]);
            leaves.SetHeaderRange(PbtKeyDerivation.BasicDataLeafKey, basicDataAndCodeHash);

            byte[]? chunks = code is { Length: > 0 } ? PbtKeyDerivation.ChunkifyCode(code) : null;
            int chunkCount = chunks is null ? 0 : chunks.Length / PbtKeyDerivation.CodeChunkSize;
            if (chunks is not null)
            {
                int headerChunks = Math.Min(chunkCount, PbtKeyDerivation.HeaderCodeChunks);
                leaves.SetHeaderRange(PbtKeyDerivation.HeaderCodeChunkSubIndex(0), ChunkRun(chunks, 0, headerChunks));
            }

            leaves.WriteHeaderStem(batch, PbtKeyDerivation.AccountHeaderStem(address));

            // the overflow chunks are content-addressed, so a code shared by several accounts is laid
            // out once per account and the writes are byte-identical
            for (int i = PbtKeyDerivation.HeaderCodeChunks; i < chunkCount;)
            {
                Stem overflowStem = PbtKeyDerivation.CodeOverflowStem(account.CodeHash.ValueHash256, i, out byte subIndex);
                int run = Math.Min(chunkCount - i, PbtKeyDerivation.StemSubtreeWidth - subIndex);
                leaves.WriteChunkRun(batch, overflowStem, subIndex, ChunkRun(chunks!, i, run));
                i += run;
            }

            pendingAccounts++;
            if (pendingAccounts >= ProgressPublishInterval)
            {
                Interlocked.Add(ref accounts, pendingAccounts);
                pendingAccounts = 0;
            }
        }

        Interlocked.Add(ref accounts, pendingAccounts);
    }

    /// <summary>Lays out one account's slots, taking them from the source reader's own storage iterator.</summary>
    /// <remarks>
    /// The iterator hands over the slot already decoded and the key already parsed, which is what keeps
    /// this blind to the source's storage key shape and slot encoding. Slots arrive grouped by address
    /// and in ascending slot order — the ordering the key deriver needs to charge one address hash per
    /// account plus one suffix hash per 256-slot run, and the ordering that keeps one storage-zone stem
    /// open at a time, since a stem covers exactly one such run.
    /// </remarks>
    private static void CopySlots(
        FlatPersistence.IPersistenceReader reader,
        IPbtPersistence.IWriteBatch batch,
        LeafBlobWriter leaves,
        in ValueHash256 accountKey,
        Address address,
        ref long slots,
        CancellationToken cancellationToken)
    {
        long pendingSlots = 0;
        PbtSlotKeyDeriver deriver = new(address);
        using FlatPersistence.IFlatIterator slotIterator = reader.CreateStorageIterator(accountKey, default, ValueKeccak.MaxValue);
        while (slotIterator.MoveNext())
        {
            cancellationToken.ThrowIfCancellationRequested();

            // preimage mode: the slot key holds the raw slot as a 32-byte big-endian value
            UInt256 slot = new(slotIterator.CurrentKey.Bytes, isBigEndian: true);
            leaves.SetSlot(batch, ref deriver, slot, slotIterator.CurrentValue);

            if (++pendingSlots >= ProgressPublishInterval)
            {
                Interlocked.Add(ref slots, pendingSlots);
                pendingSlots = 0;
            }
        }

        // the account's last storage-zone stem has no successor to close it
        leaves.WriteOpenStem(batch);
        Interlocked.Add(ref slots, pendingSlots);
    }

    /// <summary>
    /// The account-key range of one copy partition, splitting the space evenly over the first two key
    /// bytes. Preimage account keys are raw addresses, which are uniform over that prefix, so the
    /// ranges hold comparable numbers of accounts.
    /// </summary>
    private static (ValueHash256 Start, ValueHash256 End) PartitionBounds(int partition, int partitionCount)
    {
        ValueHash256 start = default;
        BinaryPrimitives.WriteUInt16BigEndian(start.BytesAsSpan, (ushort)((long)partition * PartitionPrefixSpace / partitionCount));

        if (partition == partitionCount - 1) return (start, ValueKeccak.MaxValue);

        ValueHash256 end = default;
        BinaryPrimitives.WriteUInt16BigEndian(end.BytesAsSpan, (ushort)((long)(partition + 1) * PartitionPrefixSpace / partitionCount));
        return (start, end);
    }

    /// <summary>
    /// Phase two: derives the tree leaves from the PBT flat columns and folds them.
    /// The scan is single-threaded because the fold it feeds is, and because the rebuilder's windowing
    /// only pays off while the leaves stay ordered.
    /// </summary>
    private async Task DeriveAndFold(StateId targetState, CancellationToken cancellationToken)
    {
        Channel<ArrayPoolList<RebuildEntry>> entries = Channel.CreateBounded<ArrayPoolList<RebuildEntry>>(new BoundedChannelOptions(EntryChunkCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });

        // a linked source lets the producer, parked on a full channel, unblock if the rebuilder fails
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task producer = Task.Run(() => ProduceEntries(entries.Writer, cts.Token), cts.Token);

        try
        {
            await rebuilder.Rebuild(entries.Reader, targetState, cancellationToken);
        }
        finally
        {
            await cts.CancelAsync();
            try { await producer; }
            catch { /* the failure already surfaced through the consumer above */ }
        }
    }
    /// <summary>Emits every tree leaf, in ascending stem order, by zone: the account headers, then the content-addressed overflow code chunks, then the storage slots.</summary>
    /// <remarks>
    /// The zones are disjoint subtrees and sort in that order (a stem's top nibble is its zone: 0x0
    /// accounts, 0x1 code, 0x8 storage), and each has a leaf column of its own keyed by stem — so
    /// scanning the three in turn yields a globally ascending stream with nothing left to sort.
    /// </remarks>
    private async Task ProduceEntries(ChannelWriter<ArrayPoolList<RebuildEntry>> entries, CancellationToken cancellationToken)
    {
        using LeafSink sink = new(entries, cancellationToken);
        try
        {
            await EmitZone(LeafColumn(PbtColumns.AccountLeaves), sink, cancellationToken);
            await EmitZone(LeafColumn(PbtColumns.CodeLeaves), sink, cancellationToken);
            await EmitZone(LeafColumn(PbtColumns.StorageLeaves), sink, cancellationToken);

            await sink.Complete();
            entries.TryComplete();
        }
        catch (Exception e)
        {
            entries.TryComplete(e);
        }
    }

    /// <summary>Emits the leaves of one zone's blobs, in ascending stem then sub-index order.</summary>
    /// <remarks>
    /// The leaves are read out into a buffer before any of them is handed on, and the view is closed
    /// before the first is: the enumerator and the column's value are both spans over the store, which
    /// cannot survive the <c>await</c> that a full sink chunk parks on — and that <c>await</c> waits on
    /// the fold, so a view held across it would stay open for as long as the fold takes rather than for
    /// as long as the read does.
    /// <para>
    /// The column is read live — no snapshot — in <see cref="ViewLeafChunk"/>-leaf ranges, reopening the
    /// view between them so no RocksDB superversion stays pinned across the hours-long fold. This is safe
    /// because the scan runs strictly ahead of the fold it feeds (producer → bounded channel → window →
    /// flush channel → fold), so the fold only ever rewrites the <see cref="PbtLeafFormat.LeavesOnly"/>
    /// blob of a stem the scan has already emitted. A view opened over stems not yet emitted therefore
    /// never observes a reformatted blob. Were that invariant ever broken the failure is loud, not
    /// silent: <see cref="StemLeafBlob.EnumerateLeavesOnly"/> throws on a non-leaves-only blob.
    /// </para>
    /// </remarks>
    private async Task EmitZone(ISortedKeyValueStore column, LeafSink sink, CancellationToken cancellationToken)
    {
        // a stem is read whole, so the buffer overshoots the bound by at most one stem's leaves
        using ArrayPoolList<RebuildEntry> buffered = new(ViewLeafChunk + PbtKeyDerivation.StemSubtreeWidth);
        byte[] cursor = new byte[Stem.Length];
        byte[] pastEnd = PastEveryKey();

        while (true)
        {
            byte[]? resumeFrom = ReadThroughView(column, cursor, pastEnd, buffered, cancellationToken);

            for (int i = 0; i < buffered.Count; i++)
            {
                await sink.Add(buffered[i]);
            }

            buffered.Clear();
            if (resumeFrom is null) return;

            cursor = resumeFrom;
        }
    }

    /// <summary>
    /// Reads whole stems' leaves into <paramref name="buffered"/> through one view over
    /// <c>[<paramref name="cursor"/>, <paramref name="pastEnd"/>)</c>, up to <see cref="ViewLeafChunk"/>
    /// of them; returns where to resume, or <c>null</c> when the zone is exhausted.
    /// </summary>
    /// <remarks>
    /// A stem is never split across two view openings, so no stem is ever read after the fold has
    /// reformatted its blob.
    /// </remarks>
    private byte[]? ReadThroughView(
        ISortedKeyValueStore column, byte[] cursor, byte[] pastEnd, ArrayPoolList<RebuildEntry> buffered, CancellationToken cancellationToken)
    {
        using ISortedView view = column.GetViewBetween(cursor, pastEnd);
        while (buffered.Count < ViewLeafChunk && view.MoveNext())
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadStemLeaves(view, buffered);
        }

        // the view drained before the buffer filled, so the zone is exhausted
        if (buffered.Count < ViewLeafChunk) return null;

        // the loop stopped on the count, not on MoveNext, so the view is still on the last stem read:
        // resume just past it so the reopened view's first row is the next stem
        return AfterKey(view.CurrentKey);
    }

    /// <summary>Appends the leaves of the blob the view sits on to <paramref name="buffered"/>.</summary>
    private static void ReadStemLeaves(ISortedView view, ArrayPoolList<RebuildEntry> buffered)
    {
        Stem stem = new(view.CurrentKey);
        StemLeafBlob.LeafEnumerator leaves = StemLeafBlob.EnumerateLeavesOnly(view.CurrentValue);
        while (leaves.MoveNext())
        {
            buffered.Add(new RebuildEntry(stem, leaves.CurrentSubIndex, new ValueHash256(leaves.CurrentValue)));
        }
    }

    /// <summary>
    /// One worker's scratch for laying out leaves-only blobs. An account's slots arrive with the account
    /// and in ascending order, so only two stems are ever open: its header stem, which its first 64
    /// slots share with its own fields, and whichever storage-zone stem the slots have reached.
    /// </summary>
    /// <remarks>
    /// Owned by one worker and reused across every account it copies, so the two maps are paid for once.
    /// <para>
    /// A stem's leaves accumulate in a pooled <see cref="IPbtStemChanges"/> — the same map the block path
    /// folds through — which <see cref="StemLeafBlob.ApplyNoHash"/> lays out. A
    /// <see cref="IPbtStemChanges.Set"/> may promote to a larger variant and return the old one to the
    /// pool, so its result must always be stored back.
    /// </para>
    /// </remarks>
    private sealed class LeafBlobWriter : IDisposable
    {
        private IPbtStemChanges _header = PbtStemChanges.Rent();
        private IPbtStemChanges _stem = PbtStemChanges.Rent();

        /// <summary>Meaningful only while <see cref="_stem"/> holds something.</summary>
        private Stem _openStem;

        public void BeginAccount() => Restart(ref _header);

        /// <summary>Sets a run of the header stem's leaves — its fields, or its code chunks — which are consecutive either way.</summary>
        public void SetHeaderRange(byte startSubIndex, scoped ReadOnlySpan<byte> values) => _header = _header.SetRange(startSubIndex, values);

        public void WriteHeaderStem(IPbtPersistence.IWriteBatch batch, in Stem stem) => batch.SetLeafBlob(stem, StemLeafBlob.ApplyNoHash([], _header));

        /// <summary>Routes a slot to its stem, closing the open storage-zone one when the slot has moved past it.</summary>
        public void SetSlot(IPbtPersistence.IWriteBatch batch, ref PbtSlotKeyDeriver deriver, in UInt256 slot, scoped ReadOnlySpan<byte> value)
        {
            Stem stem = deriver.Derive(slot, out byte subIndex);
            if (PbtKeyDerivation.IsHeaderSlot(slot))
            {
                _header = _header.Set(subIndex, SlotLeaf(value));
                return;
            }

            if (_stem.Count > 0 && stem != _openStem) WriteOpenStem(batch);
            _openStem = stem;
            _stem = _stem.Set(subIndex, SlotLeaf(value));
        }

        public void WriteOpenStem(IPbtPersistence.IWriteBatch batch)
        {
            if (_stem.Count == 0) return;

            batch.SetLeafBlob(_openStem, StemLeafBlob.ApplyNoHash([], _stem));
            Restart(ref _stem);
        }

        /// <summary>Lays out a whole stem of code chunks at once; the storage map is free by now, the slots having been written first.</summary>
        public void WriteChunkRun(IPbtPersistence.IWriteBatch batch, in Stem stem, byte startSubIndex, scoped ReadOnlySpan<byte> chunks)
        {
            _stem = _stem.SetRange(startSubIndex, chunks);
            batch.SetLeafBlob(stem, StemLeafBlob.ApplyNoHash([], _stem));
            Restart(ref _stem);
        }

        /// <remarks>
        /// Returned rather than cleared: the variants are size-tiered, so a map grown for a stem holding
        /// a whole contract's code would stay that large for every stem after it.
        /// </remarks>
        private static void Restart(ref IPbtStemChanges changes)
        {
            PbtStemChanges.Return(changes);
            changes = PbtStemChanges.Rent();
        }

        /// <summary>Returns the two maps the writer holds, which nothing else ever hands back.</summary>
        public void Dispose()
        {
            PbtStemChanges.Return(_header);
            PbtStemChanges.Return(_stem);
        }
    }

    /// <summary>A slot's tree leaf: the stored value re-padded to the canonical 32 bytes.</summary>
    private static ValueHash256 SlotLeaf(scoped ReadOnlySpan<byte> stored)
    {
        ValueHash256 leaf = default;
        stored.CopyTo(leaf.BytesAsSpan[(ValueHash256.MemorySize - stored.Length)..]);
        return leaf;
    }


    /// <summary>Buffers leaves into pooled chunks and hands each full chunk to the rebuilder.</summary>
    private sealed class LeafSink(ChannelWriter<ArrayPoolList<RebuildEntry>> entries, CancellationToken cancellationToken) : IDisposable
    {
        private ArrayPoolList<RebuildEntry> _chunk = new(ChunkSize);
        private bool _owned = true;

        public async ValueTask Add(RebuildEntry entry)
        {
            _chunk.Add(entry);
            if (_chunk.Count >= ChunkSize) await Flush();
        }

        public async ValueTask Complete()
        {
            if (_chunk.Count > 0) await Flush();
        }

        // ownership of a chunk passes on write; clearing the flag first means a failed write drops it
        // to the GC rather than risking a double dispose against the consumer
        private async ValueTask Flush()
        {
            _owned = false;
            await entries.WriteAsync(_chunk, cancellationToken);
            _chunk = new ArrayPoolList<RebuildEntry>(ChunkSize);
            _owned = true;
        }

        public void Dispose()
        {
            if (_owned) _chunk.Dispose();
        }
    }

    private static Account DecodeAccount(ReadOnlySpan<byte> slimRlp)
    {
        RlpReader reader = new(slimRlp);
        return AccountDecoder.Slim.Decode(ref reader)!;
    }

    /// <summary>A run of code chunks, which are leaf values back to back, out of what <see cref="PbtKeyDerivation.ChunkifyCode"/> laid out.</summary>
    private static ReadOnlySpan<byte> ChunkRun(byte[] chunks, int firstChunk, int count) =>
        chunks.AsSpan(firstChunk * PbtKeyDerivation.CodeChunkSize, count * PbtKeyDerivation.CodeChunkSize);

    /// <summary>One byte longer than the longest key any pbt column holds, so it sorts above all of them.</summary>
    private static byte[] PastEveryKey()
    {
        byte[] key = new byte[Math.Max(Stem.Length, TrieNodeKey.Length) + 1];
        key.AsSpan().Fill(0xFF);
        return key;
    }

    /// <summary>
    /// The exclusive successor of <paramref name="key"/> as an inclusive lower bound: its bytes with a
    /// <c>0x00</c> appended, which sorts strictly after it and at or before the next key, so a view
    /// opened at it starts on the key after <paramref name="key"/>.
    /// </summary>
    private static byte[] AfterKey(ReadOnlySpan<byte> key)
    {
        byte[] next = new byte[key.Length + 1];
        key.CopyTo(next);
        return next;
    }

    /// <summary>The pbt leaf column, as the sorted store its range scan reads through.</summary>
    private ISortedKeyValueStore LeafColumn(PbtColumns column) => (ISortedKeyValueStore)pbtDb.GetColumnDb(column);
}
