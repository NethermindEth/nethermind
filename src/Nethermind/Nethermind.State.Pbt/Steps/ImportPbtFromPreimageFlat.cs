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
using Nethermind.Core.Caching;
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
/// The step feeds decoded entries to <see cref="PbtRebuilder"/>, then exits the process (mirroring
/// <c>ImportFlatDb</c>).
/// </summary>
/// <remarks>
/// The source is ordered by address but a stem is a BLAKE3 digest, so a straight scan would hand the
/// rebuilder entries in random tree order and every window would read-modify-write the whole tree. The
/// import therefore runs in two phases around a throwaway scratch database (see
/// <see cref="PbtImportScratch"/>):
/// <list type="number">
/// <item>Walk the source, derive each entry's stem, and stage a record keyed by its tree key. The
/// account key space is split into ranges (as <c>FlatTrieVerifier</c> does) that workers claim one at
/// a time, each reading its accounts' storage inline, so the whole staging phase scales with the
/// available cores.</item>
/// <item>Scan the scratch database, whose ordering is stem order, and feed the decoded entries to the
/// rebuilder. Phase one stored everything the entries need, so this phase reads neither the flat
/// source nor the code database.</item>
/// </list>
/// </remarks>
[RunnerStepDependencies(
    dependencies: [typeof(InitializeBlockTree)],
    dependents: [typeof(InitializeBlockchain)]
)]
public class ImportPbtFromPreimageFlat(
    FlatPersistence flatSource,
    [KeyFilter(DbNames.Code)] IDb codeDb,
    [KeyFilter(PbtImportScratch.DbName)] IDb scratchDb,
    PbtRebuilder rebuilder,
    IPbtPersistence pbtPersistence,
    IPbtConfig config,
    IProcessExitSource exitSource,
    ILogManager logManager
) : IStep
{
    private const int AddressLength = 20;

    /// <summary>Entries per chunk on the entry channel; chunking amortizes the channel's per-write cost over the import's billions of entries.</summary>
    private const int ChunkSize = 2_048;

    /// <summary>Chunks in flight on the entry channel (~128k entries with <see cref="ChunkSize"/>).</summary>
    private const int EntryChunkCapacity = 64;

    /// <summary>Account-key ranges per staging worker. Ranges are claimed one at a time, so several per worker keep them busy to the end despite uneven storage sizes.</summary>
    private const int PartitionsPerWorker = 16;

    /// <summary>Distinct values of the two account-key bytes the staging ranges are cut on, bounding the range count.</summary>
    private const int PartitionPrefixSpace = 1 << 16;

    /// <summary>Distinct code hashes remembered for overflow-chunk dedup; bounds the cache's memory, at worst re-staging an evicted code's chunks.</summary>
    private const int SeenCodeCacheCapacity = 1 << 20;

    /// <summary>Entries a worker stages before publishing them to the shared counters, so progress moves within a range that takes minutes without an interlocked add per entry.</summary>
    private const int ProgressPublishInterval = 100_000;

    private static readonly TimeSpan StagingLogInterval = TimeSpan.FromSeconds(5);

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

        using FlatPersistence.IPersistenceReader reader = flatSource.CreateReader();
        if (!reader.IsPreimageMode)
        {
            if (_logger.IsError) _logger.Error("Source flat database is not in preimage mode; addresses and slots cannot be recovered to build PBT. Aborting.");
            exitSource.Exit(1);
            return;
        }

        if (scratchDb is not ISortedKeyValueStore sortedScratch)
        {
            if (_logger.IsError) _logger.Error($"The PBT import scratch database ({scratchDb.GetType().Name}) does not support ordered iteration. Aborting.");
            exitSource.Exit(1);
            return;
        }

        FlatStateId sourceState = reader.CurrentState;
        if (sourceState == FlatStateId.PreGenesis)
        {
            if (_logger.IsInfo) _logger.Info("Source flat database is empty; nothing to import.");
            return;
        }

        int workerCount = config.ImportStorageReadConcurrency > 0 ? config.ImportStorageReadConcurrency : Environment.ProcessorCount;
        if (_logger.IsInfo) _logger.Info($"Rebuilding PBT state from preimage-flat database at {sourceState} with {workerCount} source reader(s)");

        try
        {
            await StageScratchRecords(workerCount, cancellationToken);
            await FoldScratchRecords(sortedScratch, sourceState.BlockNumber, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (_logger.IsInfo) _logger.Info("PBT import cancelled.");
            exitSource.Exit(1);
            return;
        }

        exitSource.Exit(0);
    }

    /// <summary>
    /// Phase one: walks the flat source and stages every account, slot and code chunk into the scratch
    /// database under its tree key, which sorts them by stem.
    /// </summary>
    /// <remarks>
    /// Each worker owns a source reader, a scratch write batch and the range it is currently on, so
    /// nothing is shared but the code-hash dedup cache (which is internally synchronised) and the
    /// counters. Ranges are handed out on demand rather than assigned up front because storage sizes
    /// are wildly uneven.
    /// </remarks>
    private async Task StageScratchRecords(int workerCount, CancellationToken cancellationToken)
    {
        Stopwatch staging = Stopwatch.StartNew();
        int partitionCount = Math.Min(workerCount * PartitionsPerWorker, PartitionPrefixSpace);
        LruKeyCache<ValueHash256> seenCodeHashes = new(SeenCodeCacheCapacity, "PBT import code hashes");

        int nextPartition = -1, donePartitions = 0;
        long accounts = 0, slots = 0;

        void StagePartitions()
        {
            using FlatPersistence.IPersistenceReader reader = flatSource.CreateReader();
            using IWriteBatch batch = scratchDb.StartWriteBatch();
            PbtImportScratchWriter writer = new(batch);

            int partition;
            while ((partition = Interlocked.Increment(ref nextPartition)) < partitionCount)
            {
                (ValueHash256 start, ValueHash256 end) = PartitionBounds(partition, partitionCount);
                StagePartition(reader, writer, seenCodeHashes, start, end, ref accounts, ref slots, cancellationToken);
                Interlocked.Increment(ref donePartitions);
            }
        }

        // ProgressLogger is neither thread-safe nor self-throttling, so one ticker owns it and samples
        // the counters the workers publish, rather than the workers logging as they finish a range
        async Task LogStagingProgress(CancellationToken loggingToken)
        {
            long loggedAccounts = 0, loggedSlots = 0;
            double accountsPerSec = 0, slotsPerSec = 0;
            Stopwatch sinceLog = Stopwatch.StartNew();

            // Ranges are equal slices of the account key space and addresses are uniform over it, so
            // the fraction of ranges finished is a fair completion estimate. CurrentValue is driven by
            // the entry count instead, only so it moves every tick: the logger drops a repeated line
            // unless CurrentValue changes, which would stall the log through a long-running range.
            ProgressLogger progress = new("PBT import staging", logManager);
            progress.SetFormat(_ =>
            {
                float percentage = Math.Clamp(Volatile.Read(ref donePartitions) / (float)partitionCount, 0, 1);
                return $"PBT import staging {percentage.ToString("P2", CultureInfo.InvariantCulture),8} {Progress.GetMeter(percentage, 1)} | " +
                    $"{Interlocked.Read(ref accounts),13:N0} acc ({accountsPerSec,8:N0}/s) | {Interlocked.Read(ref slots),15:N0} slot ({slotsPerSec,8:N0}/s)";
            });
            progress.Reset(0, 0);

            using PeriodicTimer timer = new(StagingLogInterval);
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
            try { await LogStagingProgress(loggingCts.Token); }
            catch (OperationCanceledException) { /* staging finished */ }
        }, CancellationToken.None);

        Task[] workers = new Task[workerCount];
        for (int i = 0; i < workerCount; i++)
        {
            workers[i] = Task.Run(StagePartitions, cancellationToken);
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

        scratchDb.Flush();
        if (_logger.IsInfo) _logger.Info($"PBT import staged {accounts:N0} accounts and {slots:N0} slots in {staging.Elapsed:hh\\:mm\\:ss}. The scratch database is left on disk and is safe to delete once the import finishes.");
    }

    /// <summary>
    /// Stages every account in <c>[<paramref name="start"/>, <paramref name="end"/>]</c>, and each one's
    /// storage inline, publishing what it stages into the shared <paramref name="accounts"/> and
    /// <paramref name="slots"/> counters as it goes.
    /// </summary>
    private void StagePartition(
        FlatPersistence.IPersistenceReader reader,
        PbtImportScratchWriter writer,
        LruKeyCache<ValueHash256> seenCodeHashes,
        ValueHash256 start,
        ValueHash256 end,
        ref long accounts,
        ref long slots,
        CancellationToken cancellationToken)
    {
        long pendingAccounts = 0;
        using FlatPersistence.IFlatIterator accountIterator = reader.CreateAccountIterator(start, end);
        while (accountIterator.MoveNext())
        {
            cancellationToken.ThrowIfCancellationRequested();

            // preimage mode: the flat key holds the raw address in its first 20 bytes
            ValueHash256 accountKey = accountIterator.CurrentKey;
            Address address = new(accountKey.Bytes[..AddressLength]);
            ReadOnlySpan<byte> slimRlp = accountIterator.CurrentValue;
            Account account = DecodeAccount(slimRlp);

            byte[]? code = account.HasCode
                ? codeDb.Get(account.CodeHash.Bytes) ?? throw new InvalidDataException($"Missing bytecode for {address} (code hash {account.CodeHash}) in the code database.")
                : null;

            // the deriver owns the address hash for both the header leaves and the storage stems
            PbtSlotKeyDeriver deriver = new(address);

            // overflow code chunks are content-addressed by code hash, so duplicate contracts
            // (proxies) would re-stage identical leaves — only the first occurrence stages them
            bool stageOverflowChunks = code is null || seenCodeHashes.Set(account.CodeHash);
            writer.WriteAccount(address, account, slimRlp, code, deriver.AddressPrefix(), stageOverflowChunks);
            pendingAccounts++;

            if (account.HasStorage) StageStorage(reader, writer, address, accountKey, ref deriver, ref slots, cancellationToken);

            if (pendingAccounts >= ProgressPublishInterval)
            {
                Interlocked.Add(ref accounts, pendingAccounts);
                pendingAccounts = 0;
            }
        }

        Interlocked.Add(ref accounts, pendingAccounts);
    }

    private static void StageStorage(
        FlatPersistence.IPersistenceReader reader,
        PbtImportScratchWriter writer,
        Address address,
        in ValueHash256 accountKey,
        ref PbtSlotKeyDeriver deriver,
        ref long slots,
        CancellationToken cancellationToken)
    {
        long pendingSlots = 0;
        using FlatPersistence.IFlatIterator storageIterator = reader.CreateStorageIterator(accountKey, ValueKeccak.Zero, ValueKeccak.MaxValue);
        while (storageIterator.MoveNext())
        {
            cancellationToken.ThrowIfCancellationRequested();

            // preimage mode: the flat slot key holds the raw slot as a 32-byte big-endian value
            UInt256 slot = new(storageIterator.CurrentKey.Bytes, isBigEndian: true);
            EvmWord word = EvmWordSlot.FromStripped(storageIterator.CurrentValue);
            writer.WriteSlot(address, slot, word, ref deriver);

            if (++pendingSlots >= ProgressPublishInterval)
            {
                Interlocked.Add(ref slots, pendingSlots);
                pendingSlots = 0;
            }
        }

        Interlocked.Add(ref slots, pendingSlots);
    }

    /// <summary>
    /// The account-key range of one staging partition, splitting the space evenly over the first two
    /// key bytes. Preimage account keys are raw addresses, which are uniform over that prefix, so the
    /// ranges hold comparable numbers of accounts.
    /// </summary>
    private static (ValueHash256 Start, ValueHash256 End) PartitionBounds(int partition, int partitionCount)
    {
        ValueHash256 start = default;
        BinaryPrimitives.WriteUInt16BigEndian(start.BytesAsSpan, (ushort)((long)partition * PartitionPrefixSpace / partitionCount));

        // the last range runs to the top of the key space rather than to a prefix boundary
        if (partition == partitionCount - 1) return (start, ValueKeccak.MaxValue);

        ValueHash256 end = default;
        BinaryPrimitives.WriteUInt16BigEndian(end.BytesAsSpan, (ushort)((long)(partition + 1) * PartitionPrefixSpace / partitionCount));
        return (start, end);
    }

    /// <summary>
    /// Phase two: scans the scratch database in stem order and folds the decoded entries into the tree.
    /// The scan is single-threaded because there is nothing left to parallelize — every source read,
    /// hash and code chunking already happened in phase one — and because the rebuilder's windowing
    /// only pays off while the entries stay ordered.
    /// </summary>
    private async Task FoldScratchRecords(ISortedKeyValueStore scratch, ulong blockNumber, CancellationToken cancellationToken)
    {
        Channel<ArrayPoolList<RebuildEntry>> entries = Channel.CreateBounded<ArrayPoolList<RebuildEntry>>(new BoundedChannelOptions(EntryChunkCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });

        // a linked source lets the producer, parked on a full channel, unblock if the rebuilder fails
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task producer = Task.Run(() => DecodeScratch(scratch, entries.Writer, cts.Token), cts.Token);

        try
        {
            await rebuilder.Rebuild(entries.Reader, blockNumber, cancellationToken);
        }
        finally
        {
            await cts.CancelAsync();
            try { await producer; }
            catch { /* the failure already surfaced through the consumer above */ }
        }
    }

    private async Task DecodeScratch(ISortedKeyValueStore scratch, ChannelWriter<ArrayPoolList<RebuildEntry>> entries, CancellationToken cancellationToken)
    {
        ArrayPoolList<RebuildEntry> chunk = new(ChunkSize);
        bool owned = true;

        // ownership of a chunk passes on write; clearing the flag first means a failed write drops
        // it to the GC rather than risking a double dispose against the consumer
        async ValueTask FlushChunk()
        {
            owned = false;
            await entries.WriteAsync(chunk, cancellationToken);
            chunk = new(ChunkSize);
            owned = true;
        }

        try
        {
            byte[] startKey = new byte[PbtImportScratch.KeyLength];

            // one byte longer than any record key, so it sorts above every one of them
            byte[] endKey = new byte[PbtImportScratch.KeyLength + 1];
            endKey.AsSpan().Fill(0xFF);

            using ISortedView view = scratch.GetViewBetween(startKey, endKey);
            while (view.MoveNext())
            {
                cancellationToken.ThrowIfCancellationRequested();

                chunk.Add(PbtImportScratch.Decode(view.CurrentKey, view.CurrentValue));
                if (chunk.Count >= ChunkSize) await FlushChunk();
            }

            if (chunk.Count > 0) await FlushChunk();
            entries.TryComplete();
        }
        catch (Exception e)
        {
            entries.TryComplete(e);
        }
        finally
        {
            if (owned) chunk.Dispose();
        }
    }

    private static Account DecodeAccount(ReadOnlySpan<byte> slimRlp)
    {
        RlpReader reader = new(slimRlp);
        return AccountDecoder.Slim.Decode(ref reader)!;
    }
}
