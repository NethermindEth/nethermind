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

/// <summary>Rebuilds PBT state from a preimage-flat database, then exits.</summary>
/// <remarks>
/// Raw-address source order differs from hash-derived stem order, so phase one writes
/// <see cref="PbtLeafFormat.LeavesOnly"/> blobs keyed by stem and phase two folds their ordered scans.
/// The phases cannot overlap because address partitions scatter across the entire stem space.
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
    PbtRocksDbPersistence pbtPersistence,
    IPbtConfig config,
    IProcessExitSource exitSource,
    ILogManager logManager
) : IStep
{
    private const int AddressLength = 20;

    /// <summary>Entries per leaf-channel chunk, amortizing channel write costs.</summary>
    private const int ChunkSize = 2_048;

    /// <summary>Maximum chunks in flight on the leaf channel.</summary>
    private const int EntryChunkCapacity = 64;

    /// <summary>Account-key ranges per worker to balance uneven storage sizes.</summary>
    private const int PartitionsPerWorker = 16;

    /// <summary>Number of two-byte account-key prefixes used to bound the range count.</summary>
    private const int PartitionPrefixSpace = 1 << 16;

    /// <summary>Entries copied before workers publish progress, avoiding an interlocked add per entry.</summary>
    private const int ProgressPublishInterval = 100_000;

    private static readonly TimeSpan CopyLogInterval = TimeSpan.FromSeconds(5);

    /// <summary>Leaves read per phase-two view to bound how long a RocksDB superversion is pinned.</summary>
    /// <remarks>One stem can contain up to <see cref="PbtKeyDerivation.StemSubtreeWidth"/> leaves.</remarks>
    internal int ViewLeafChunk { get; init; } = EntryChunkCapacity * ChunkSize;

    /// <summary>Keys deleted per view and write batch when clearing an interrupted import.</summary>
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
        // Keep the snapshot only long enough to validate the source and read its state.
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

            // State is addressed by the source block header's root; the fold records its tree root beside it.
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
    /// An interrupted import can leave leaf blobs and trie nodes despite a pre-genesis state pointer.
    /// <see cref="TrieUpdater"/> reads a stored root group before its supplied root hash, so stale nodes
    /// would produce the wrong root. Each deletion chunk closes its view before committing to avoid
    /// pinning RocksDB versions throughout the sweep.
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

                    // The count limit leaves the view on the last deleted key.
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
    /// Workers claim ranges on demand to balance uneven storage. Batches retain a pre-genesis state
    /// pointer with <see cref="WriteFlags.DisableWAL"/>; a crash leaves deterministic blobs that the
    /// next import safely overwrites.
    /// </remarks>
    private async Task CopyFlatColumns(int workerCount, CancellationToken cancellationToken)
    {
        Stopwatch copying = Stopwatch.StartNew();
        int partitionCount = Math.Min(workerCount * PartitionsPerWorker, PartitionPrefixSpace);

        int nextPartition = -1, donePartitions = 0;
        long accounts = 0, slots = 0;

        void CopyPartitions()
        {
            int partition;
            while ((partition = Interlocked.Increment(ref nextPartition)) < partitionCount)
            {
                (ValueHash256 start, ValueHash256 end) = PartitionBounds(partition, partitionCount);

                // Limit each source snapshot and write batch to one range.
                using (FlatPersistence.IPersistenceReader reader = flatSource.CreateReader())
                using (IPbtPersistence.IWriteBatch batch = pbtPersistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, default, WriteFlags.DisableWAL))
                {
                    CopyAccounts(reader, batch, start, end, ref accounts, ref slots, cancellationToken);
                }

                Interlocked.Increment(ref donePartitions);
            }
        }

        // ProgressLogger is not thread-safe, so one ticker samples worker-published counters.
        async Task LogCopyProgress(CancellationToken loggingToken)
        {
            long loggedAccounts = 0, loggedSlots = 0;
            double accountsPerSec = 0, slotsPerSec = 0;
            Stopwatch sinceLog = Stopwatch.StartNew();

            // CurrentValue uses entry count so ProgressLogger emits updates during long ranges.
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

        // Batches skipped the WAL; flush before phase two reads them.
        pbtDb.Flush();
        if (_logger.IsInfo) _logger.Info($"PBT import copied {accounts:N0} accounts and {slots:N0} slots in {copying.Elapsed:hh\\:mm\\:ss}.");
    }

    /// <summary>
    /// Writes each account's header stem blob — its <c>BASIC_DATA</c>, <c>CODE_HASH</c>, its first 64
    /// storage slots and its header code chunks, all of which share that stem — plus the
    /// content-addressed overflow chunks of any code too long to fit it.
    /// </summary>
    /// <remarks>Slots are read first so each header stem is written once, complete.</remarks>
    private void CopyAccounts(
        FlatPersistence.IPersistenceReader reader,
        IPbtPersistence.IWriteBatch batch,
        ValueHash256 start,
        ValueHash256 end,
        ref long accounts,
        ref long slots,
        CancellationToken cancellationToken)
    {
        long pendingAccounts = 0;
        Span<byte> basicData = stackalloc byte[ValueHash256.MemorySize];
        using FlatPersistence.IFlatIterator accountIterator = reader.CreateAccountIterator(start, end);
        while (accountIterator.MoveNext())
        {
            cancellationToken.ThrowIfCancellationRequested();

            // In preimage mode, the first 20 key bytes are the raw address.
            ValueHash256 accountKey = accountIterator.CurrentKey;
            Address address = new(accountKey.Bytes[..AddressLength]);

            Account account = DecodeAccount(accountIterator.CurrentValue);
            byte[]? code = account.HasCode
                ? codeDb.Get(account.CodeHash.Bytes) ?? throw new InvalidDataException($"Missing bytecode for {address} (code hash {account.CodeHash}) in the code database.")
                : null;

            if (account.HasStorage) CopySlots(reader, batch, accountKey, address, ref slots, cancellationToken);

            PbtKeyDerivation.PackBasicData(basicData, code is null ? 0u : (uint)code.Length, account.Nonce, account.Balance);
            batch.SetLeaf(PbtStateKey.Account(address, PbtKeyDerivation.BasicDataLeafKey), new ValueHash256(basicData));
            batch.SetLeaf(PbtStateKey.Account(address, PbtKeyDerivation.CodeHashLeafKey), account.CodeHash.ValueHash256);

            if (code is { Length: > 0 })
            {
                byte[] chunks = PbtKeyDerivation.ChunkifyCode(code);
                int chunkCount = chunks.Length / PbtKeyDerivation.CodeChunkSize;
                for (int i = 0; i < chunkCount; i++)
                {
                    ValueHash256 chunk = new(chunks.AsSpan(i * PbtKeyDerivation.CodeChunkSize, PbtKeyDerivation.CodeChunkSize));
                    if (chunk != default) batch.SetLeaf(PbtStateKey.Code(address, account.CodeHash.ValueHash256, i), chunk);
                }
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
    /// <remarks>Ascending slots let the key deriver reuse one address hash and one suffix hash per 256-slot run.</remarks>
    private static void CopySlots(
        FlatPersistence.IPersistenceReader reader,
        IPbtPersistence.IWriteBatch batch,
        in ValueHash256 accountKey,
        Address address,
        ref long slots,
        CancellationToken cancellationToken)
    {
        long pendingSlots = 0;
        using FlatPersistence.IFlatIterator slotIterator = reader.CreateStorageIterator(accountKey, default, ValueKeccak.MaxValue);
        while (slotIterator.MoveNext())
        {
            cancellationToken.ThrowIfCancellationRequested();

            // In preimage mode, the key is the raw 32-byte big-endian slot.
            UInt256 slot = new(slotIterator.CurrentKey.Bytes, isBigEndian: true);
            ValueHash256 value = SlotLeaf(slotIterator.CurrentValue);
            if (value != default) batch.SetLeaf(PbtStateKey.Storage(address, slot), value);

            if (++pendingSlots >= ProgressPublishInterval)
            {
                Interlocked.Add(ref slots, pendingSlots);
                pendingSlots = 0;
            }
        }

        Interlocked.Add(ref slots, pendingSlots);
    }

    /// <summary>Returns a partition over the first two raw-address bytes, which distributes accounts evenly.</summary>
    private static (ValueHash256 Start, ValueHash256 End) PartitionBounds(int partition, int partitionCount)
    {
        ValueHash256 start = default;
        BinaryPrimitives.WriteUInt16BigEndian(start.BytesAsSpan, (ushort)((long)partition * PartitionPrefixSpace / partitionCount));

        if (partition == partitionCount - 1) return (start, ValueKeccak.MaxValue);

        ValueHash256 end = default;
        BinaryPrimitives.WriteUInt16BigEndian(end.BytesAsSpan, (ushort)((long)(partition + 1) * PartitionPrefixSpace / partitionCount));
        return (start, end);
    }

    /// <summary>Derives ordered tree leaves from PBT flat columns and folds them.</summary>
    private async Task DeriveAndFold(StateId targetState, CancellationToken cancellationToken)
    {
        Channel<ArrayPoolList<RebuildEntry>> entries = Channel.CreateBounded<ArrayPoolList<RebuildEntry>>(new BoundedChannelOptions(EntryChunkCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });

        // Unblocks a producer waiting on a full channel if rebuilding fails.
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
    /// <remarks>Disjoint account, code, and storage subtrees sort in this order, yielding a global ordered stream.</remarks>
    private async Task ProduceEntries(ChannelWriter<ArrayPoolList<RebuildEntry>> entries, CancellationToken cancellationToken)
    {
        using LeafSink sink = new(entries, cancellationToken);
        try
        {
            using IPbtPersistence.IReader reader = pbtPersistence.CreateReader();
            foreach ((PbtFullKey key, ValueHash256 value) in reader.EnumerateLeaves())
            {
                cancellationToken.ThrowIfCancellationRequested();
                await sink.Add(new RebuildEntry(key, value));
            }

            await sink.Complete();
            entries.TryComplete();
        }
        catch (Exception e)
        {
            entries.TryComplete(e);
        }
    }

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

        // Ownership transfers on write; clear first to avoid double disposal if it fails.
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

    private static ReadOnlySpan<byte> ChunkRun(byte[] chunks, int firstChunk, int count) =>
        chunks.AsSpan(firstChunk * PbtKeyDerivation.CodeChunkSize, count * PbtKeyDerivation.CodeChunkSize);

    private static byte[] PastEveryKey()
    {
        byte[] key = new byte[Math.Max(Stem.Length, TrieNodeKey.Length) + 1];
        key.AsSpan().Fill(0xFF);
        return key;
    }

    /// <summary>Returns the inclusive lower bound immediately after <paramref name="key"/>.</summary>
    private static byte[] AfterKey(ReadOnlySpan<byte> key)
    {
        byte[] next = new byte[key.Length + 1];
        key.CopyTo(next);
        return next;
    }

    private ISortedKeyValueStore LeafColumn(PbtColumns column) => (ISortedKeyValueStore)pbtDb.GetColumnDb(column);
}
