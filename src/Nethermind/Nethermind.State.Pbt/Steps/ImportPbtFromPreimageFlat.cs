// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using Autofac.Features.AttributeFilters;
using Nethermind.Api.Steps;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Buffers;
using Nethermind.Evm.CodeAnalysis;
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
/// Logical accounts, storage and whole code are staged before phase two derives and folds tree leaves.
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

    /// <summary>Entries per channel chunk, amortizing channel write costs.</summary>
    private const int ChunkSize = 2_048;

    /// <summary>Maximum chunks in flight on the entry channel.</summary>
    private const int EntryChunkCapacity = 64;

    /// <summary>Account-key ranges per worker to balance uneven storage sizes.</summary>
    private const int PartitionsPerWorker = 16;

    /// <summary>Number of two-byte account-key prefixes used to bound the range count.</summary>
    private const int PartitionPrefixSpace = 1 << 16;

    /// <summary>Entries copied before workers publish progress, avoiding an interlocked add per entry.</summary>
    private const int ProgressPublishInterval = 100_000;

    private static readonly TimeSpan CopyLogInterval = TimeSpan.FromSeconds(5);

    /// <summary>Leaves per phase-two channel chunk and records per scan page.</summary>
    internal int EntryChunkSize { get; init; } = ChunkSize;

    /// <summary>Keys deleted per view and write batch when clearing an interrupted import.</summary>
    internal int ClearKeyChunk { get; init; } = 10_000;

    /// <summary>Writes per phase-one batch, bounding retained storage keys even within one account.</summary>
    internal int CopyBatchSize { get; init; } = 10_000;

    private readonly ILogger _logger = logManager.GetClassLogger<ImportPbtFromPreimageFlat>();

    public async Task Execute(CancellationToken cancellationToken)
    {
        if (pbtPersistence.IsValid)
        {
            using IPbtPersistence.IReader pbtReader = pbtPersistence.CreateReader();
            if (_logger.IsInfo) _logger.Info($"PBT state already populated ({pbtReader.CurrentState}); skipping preimage-flat import.");
            return;
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
            await DeriveAndFold(new StateId(sourceState.BlockNumber, sourceState.StateRoot), workerCount, cancellationToken);
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
    /// An interrupted import can leave logical entries and trie nodes despite a pre-genesis state pointer.
    /// <see cref="TrieUpdater"/> reads a stored root group before its supplied root hash, so stale nodes
    /// would produce the wrong root. Each deletion chunk closes its view before committing to avoid
    /// pinning RocksDB versions throughout the sweep.
    /// </remarks>
    private void ClearInterruptedAttempt()
    {
        long cleared = 0;
        byte[] pastEnd = PastEveryKey();
        pbtDb.GetColumnDb(PbtColumns.Metadata).Remove(PbtRocksDbPersistence.RootNodeGroupKey);

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
    /// Phase one: stages accounts, slots and whole code in their typed flat columns.
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

                // Limit each source snapshot to one range.
                using (FlatPersistence.IPersistenceReader reader = flatSource.CreateReader())
                using (CopyBatch batch = new(pbtPersistence, CopyBatchSize))
                {
                    CopyAccounts(reader, batch, start, end, ref accounts, ref slots, cancellationToken);
                    batch.Commit();
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

    /// <summary>Copies whole accounts and their code, with storage in its own column.</summary>
    private void CopyAccounts(
        FlatPersistence.IPersistenceReader reader,
        CopyBatch batch,
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

            // In preimage mode, the first 20 key bytes are the raw address.
            ValueHash256 accountKey = accountIterator.CurrentKey;
            Address address = new(accountKey.Bytes[..AddressLength]);

            Account account = DecodeAccount(accountIterator.CurrentValue);
            byte[]? code = account.HasCode
                ? codeDb.Get(account.CodeHash.Bytes) ?? throw new InvalidDataException($"Missing bytecode for {address} (code hash {account.CodeHash}) in the code database.")
                : null;

            if (account.HasStorage) CopySlots(reader, batch, accountKey, address, ref slots, cancellationToken);

            batch.NextWrite().SetAccount(PbtKeyDerivation.AddressKeyHash(address), account);
            if (code is not null) batch.NextWrite().SetCode(account.CodeHash.ValueHash256, new CodeInfo(code));

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
        CopyBatch batch,
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
            EvmWord value = EvmWordSlot.FromStripped(slotIterator.CurrentValue);
            batch.NextWrite().SetSlot(PbtStateKey.Storage(address, slot), value);

            if (++pendingSlots >= ProgressPublishInterval)
            {
                Interlocked.Add(ref slots, pendingSlots);
                pendingSlots = 0;
            }
        }

        Interlocked.Add(ref slots, pendingSlots);
    }

    private sealed class CopyBatch(PbtRocksDbPersistence persistence, int maxWrites) : IDisposable
    {
        private IPbtPersistence.IWriteBatch? _batch;
        private int _writes;

        public IPbtPersistence.IWriteBatch NextWrite()
        {
            if (_writes == maxWrites)
            {
                Commit();
                _batch!.Dispose();
                _batch = null;
                _writes = 0;
            }

            _batch ??= persistence.CreateStagingWriteBatch(WriteFlags.DisableWAL);
            _writes++;
            return _batch;
        }

        public void Commit() => _batch?.Commit();

        public void Dispose() => _batch?.Dispose();
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

    /// <summary>Derives tree leaves in parallel and commits bounded fold windows.</summary>
    private async Task DeriveAndFold(StateId targetState, int workerCount, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(EntryChunkSize);
        ArgumentOutOfRangeException.ThrowIfNegative(config.ImportWindowSize);
        Channel<ArrayPoolList<RebuildEntry>> entries = Channel.CreateBounded<ArrayPoolList<RebuildEntry>>(new BoundedChannelOptions(EntryChunkCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ExceptionDispatchInfo? producerFailure = null;
        Task producer = ProduceEntries();
        ExceptionDispatchInfo? consumerFailure = null;
        try
        {
            await rebuilder.Rebuild(entries.Reader, targetState, cts.Token, config.ImportWindowSize);
        }
        catch (Exception exception)
        {
            consumerFailure = ExceptionDispatchInfo.Capture(exception);
        }
        finally
        {
            await cts.CancelAsync();
            await producer;
            while (entries.Reader.TryRead(out ArrayPoolList<RebuildEntry>? chunk)) chunk.Dispose();
        }

        producerFailure?.Throw();
        consumerFailure?.Throw();

        async Task ProduceEntries()
        {
            int partitionCount = (int)Math.Min((long)workerCount * PartitionsPerWorker, PartitionPrefixSpace);
            int nextPartition = -1;
            Task[] workers = new Task[workerCount];
            for (int worker = 0; worker < workers.Length; worker++)
            {
                workers[worker] = Task.Run(async () =>
                {
                    try
                    {
                        using EntrySink sink = new(entries.Writer, EntryChunkSize, cts.Token);
                        int partition;
                        while ((partition = Interlocked.Increment(ref nextPartition)) < partitionCount * 3)
                        {
                            int zone = partition / partitionCount;
                            (byte[] start, byte[] end) = ScanBounds(partition % partitionCount, partitionCount, zone);
                            if (zone == 0) await EmitAccounts(start, end, sink, cts.Token);
                            else await EmitStorage(start, end, sink, cts.Token);
                        }
                        await sink.Complete();
                    }
                    catch (OperationCanceledException) when (cts.IsCancellationRequested)
                    {
                        // The caller or another pipeline task already stopped the import.
                    }
                    catch (Exception exception)
                    {
                        Interlocked.CompareExchange(ref producerFailure, ExceptionDispatchInfo.Capture(exception), null);
                        await cts.CancelAsync();
                    }
                }, CancellationToken.None);
            }
            await Task.WhenAll(workers);
            entries.Writer.TryComplete(producerFailure?.SourceException);
        }
    }

    private static (byte[] Start, byte[] End) ScanBounds(int partition, int partitionCount, int zone)
    {
        int prefixOffset = zone == 0 ? 0 : 1;
        byte[] start = new byte[prefixOffset + sizeof(ushort)];
        if (zone != 0) start[0] = zone == 1 ? Eip8297KeyDerivation.AccountZone : Eip8297KeyDerivation.StorageZone;
        BinaryPrimitives.WriteUInt16BigEndian(start.AsSpan(prefixOffset), (ushort)((long)partition * PartitionPrefixSpace / partitionCount));
        if (partition == partitionCount - 1)
            return (start, zone == 1 ? [Eip8297KeyDerivation.CodeZone] : PastEveryKey());

        byte[] end = (byte[])start.Clone();
        BinaryPrimitives.WriteUInt16BigEndian(end.AsSpan(prefixOffset), (ushort)((long)(partition + 1) * PartitionPrefixSpace / partitionCount));
        return (start, end);
    }

    private async Task EmitAccounts(byte[] cursor, byte[] end, EntrySink sink, CancellationToken cancellationToken)
    {
        ISortedKeyValueStore accounts = (ISortedKeyValueStore)pbtDb.GetColumnDb(PbtColumns.Accounts);
        IDb codes = pbtDb.GetColumnDb(PbtColumns.Codes);
        using ArrayPoolList<KeyValuePair<ValueHash256, Account>> buffered = new(EntryChunkSize);
        while (true)
        {
            byte[]? resumeFrom = null;
            using (ISortedView view = accounts.GetViewBetween(cursor, end))
            {
                while (buffered.Count < EntryChunkSize && view.MoveNext())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    RlpReader accountReader = new(view.CurrentValue);
                    Account account = AccountDecoder.Instance.Decode(ref accountReader)
                        ?? throw new InvalidDataException("Invalid staged PBT account.");
                    buffered.Add(new(new ValueHash256(view.CurrentKey), account));
                }
                if (buffered.Count == EntryChunkSize) resumeFrom = AfterKey(view.CurrentKey);
            }

            for (int index = 0; index < buffered.Count; index++)
            {
                (ValueHash256 addressHash, Account account) = buffered[index];
                cancellationToken.ThrowIfCancellationRequested();
                CodeInfo? code = null;
                if (account.HasCode)
                {
                    byte[] bytes = codes.Get(account.CodeHash.Bytes)
                        ?? throw new InvalidDataException($"Missing staged bytecode for account {addressHash}.");
                    code = new CodeInfo(bytes);
                }
                foreach ((PbtFullKey key, ValueHash256 value) in PbtFlatState.AccountLeaves(addressHash, account, code))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await sink.Add(new RebuildEntry((PbtStorageFullKey)key, value));
                }
            }
            buffered.Clear();
            if (resumeFrom is null) return;
            cursor = resumeFrom;
        }
    }

    private async Task EmitStorage(byte[] cursor, byte[] end, EntrySink sink, CancellationToken cancellationToken)
    {
        ISortedKeyValueStore storage = (ISortedKeyValueStore)pbtDb.GetColumnDb(PbtColumns.Storages);
        using ArrayPoolList<RebuildEntry> buffered = new(EntryChunkSize);
        while (true)
        {
            byte[]? resumeFrom = null;
            using (ISortedView view = storage.GetViewBetween(cursor, end))
            {
                while (buffered.Count < EntryChunkSize && view.MoveNext())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (view.CurrentValue.Length != ValueHash256.MemorySize)
                        throw new InvalidDataException("Invalid staged PBT storage value length.");
                    buffered.Add(new(new PbtStorageFullKey(view.CurrentKey), new ValueHash256(view.CurrentValue)));
                }
                if (buffered.Count == EntryChunkSize) resumeFrom = AfterKey(view.CurrentKey);
            }
            for (int index = 0; index < buffered.Count; index++) await sink.Add(buffered[index]);
            buffered.Clear();
            if (resumeFrom is null) return;
            cursor = resumeFrom;
        }
    }

    /// <summary>Buffers leaves into pooled chunks and hands each full chunk to the rebuilder.</summary>
    private sealed class EntrySink(ChannelWriter<ArrayPoolList<RebuildEntry>> entries, int chunkSize, CancellationToken cancellationToken) : IDisposable
    {
        private ArrayPoolList<RebuildEntry> _chunk = new(chunkSize);
        private bool _owned = true;

        public async ValueTask Add(RebuildEntry entry)
        {
            _chunk.Add(entry);
            if (_chunk.Count >= chunkSize) await Flush();
        }

        public async ValueTask Complete()
        {
            if (_chunk.Count > 0) await Flush();
        }

        // A failed channel write leaves ownership with this sink.
        private async ValueTask Flush()
        {
            await entries.WriteAsync(_chunk, cancellationToken);
            _owned = false;
            _chunk = new ArrayPoolList<RebuildEntry>(chunkSize);
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

    private static byte[] PastEveryKey()
    {
        byte[] key = new byte[PbtStorageFullKey.MaxLength + 1];
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

}
