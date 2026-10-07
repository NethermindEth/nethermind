// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using Autofac.Features.AttributeFilters;
using Nethermind.Api.Steps;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Collections;
using Nethermind.Core.Buffers;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Db;
using Nethermind.Init.Steps;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Pbt;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Pbt.Image;
using Nethermind.State.Pbt.Persistence;
using FlatPersistence = Nethermind.State.Flat.Persistence.IPersistence;
using FlatStateId = Nethermind.State.Flat.StateId;

namespace Nethermind.State.Pbt.Steps;

/// <summary>Rebuilds PBT state from a preimage-flat database.</summary>
/// <remarks>
/// Logical accounts, storage and whole code are staged before phase two derives and folds tree leaves.
/// The phases cannot overlap because address partitions scatter across the entire stem space.
/// </remarks>
[StepCommand("import-pbt", "Rebuild the PBT state from a preimage-flat database.")]
[RunnerStepDependencies(typeof(InitializeBlockTree), typeof(StartMonitoring))]
public class ImportPbtFromPreimageFlat(
    FlatPersistence flatSource,
    [KeyFilter(DbNames.Code)] IDb codeDb,
    IColumnsDb<PbtColumns> pbtDb,
    PbtRebuilder rebuilder,
    PbtRocksDbPersistence pbtPersistence,
    IPbtConfig config,
    ILogManager logManager
) : IStep
{
    /// <summary>Maximum chunks in flight on the entry channel.</summary>
    private const int EntryChunkCapacity = 64;

    /// <summary>Account-key ranges per worker to balance uneven storage sizes.</summary>
    private const int PartitionsPerWorker = 16;

    /// <summary>Entries copied before workers publish progress, avoiding an interlocked add per entry.</summary>
    private const int ProgressPublishInterval = 100_000;

    /// <summary>Code hashes already staged with their stem, so shared bytecode is fetched and written once.</summary>
    private const int StagedCodeCapacity = 1 << 20;

    private static readonly TimeSpan CopyLogInterval = TimeSpan.FromSeconds(5);

    private const string CopyPhase = "PBT import flat copy";

    /// <summary>Leaves per phase-two channel chunk and records per scan page.</summary>
    internal int EntryChunkSize { get; init; } = 2_048;

    /// <summary>Keys deleted per view and write batch when clearing an interrupted import.</summary>
    internal int ClearKeyChunk { get; init; } = 10_000;

    /// <summary>Writes per phase-one batch, bounding retained storage keys even within one account.</summary>
    internal int CopyBatchSize { get; init; } = 10_000;

    private readonly ILogger _logger = logManager.GetClassLogger<ImportPbtFromPreimageFlat>();
    private readonly LruCache<ValueHash256, PbtAccount> _stagedCodes = new(StagedCodeCapacity, nameof(_stagedCodes));
    private readonly Lock[] _codeLocks = Enumerable.Range(0, 256).Select(static _ => new Lock()).ToArray();

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
                throw new InvalidConfigurationException(
                    "Source flat database is not in preimage mode; addresses and slots cannot be recovered to build PBT.",
                    ExitCodes.ForbiddenOptionValue);

            sourceState = reader.CurrentState;
        }

        if (sourceState == FlatStateId.PreGenesis)
        {
            if (_logger.IsInfo) _logger.Info("Source flat database is empty; nothing to import.");
            return;
        }

        int workerCount = config.ImportStorageReadConcurrency > 0 ? config.ImportStorageReadConcurrency : Environment.ProcessorCount;
        int partitionCount = (int)Math.Min((long)workerCount * PartitionsPerWorker, PbtPrefixPartitions.PrefixSpace);
        if (_logger.IsInfo) _logger.Info($"Rebuilding PBT state from preimage-flat database at {sourceState} with {workerCount} source reader(s)");

        ClearInterruptedAttempt();
        await CopyFlatColumns(workerCount, partitionCount, cancellationToken);

        // State is addressed by the source block header's root; the fold records its tree root beside it.
        await DeriveAndFold(new StateId(sourceState.BlockNumber, sourceState.StateRoot), workerCount, partitionCount, cancellationToken);
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
        pbtDb.GetColumnDb(PbtColumns.Metadata).Remove(PbtRocksDbPersistence.RootNodeGroupKey);

        foreach (PbtColumns column in Enum.GetValues<PbtColumns>())
        {
            if (column == PbtColumns.Metadata) continue;
            cleared += PbtColumnSweep.DeleteKeys(pbtDb.GetColumnDb(column), ClearKeyChunk, static _ => false, CancellationToken.None);
        }

        if (cleared > 0 && _logger.IsInfo) _logger.Info($"Discarded {cleared:N0} entries left by an interrupted PBT import.");
    }

    /// <summary>
    /// Phase one: stages accounts, slots and whole code in their typed flat columns.
    /// </summary>
    /// <remarks>
    /// Workers claim ranges on demand to balance uneven storage. Batches retain a pre-genesis state
    /// pointer with <see cref="WriteFlags.DisableWAL"/>; a crash leaves deterministic blobs that the
    /// next import safely overwrites. Auto-compaction is disabled during the copy and a single full
    /// compaction runs once it completes.
    /// </remarks>
    private async Task CopyFlatColumns(int workerCount, int partitionCount, CancellationToken cancellationToken)
    {
        Stopwatch copying = Stopwatch.StartNew();
        PbtPrefixPartitions partitions = new(partitionCount);

        int nextPartition = -1, donePartitions = 0;
        long accounts = 0, slots = 0;

        void CopyPartitions()
        {
            int partition;
            while ((partition = Interlocked.Increment(ref nextPartition)) < partitionCount)
            {
                (ValueHash256 start, ValueHash256 end) = partitions.Bounds(partition);

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
            // CurrentValue uses entry count so ProgressLogger emits updates during long ranges.
            ProgressLogger progress = new(CopyPhase, logManager);
            Func<string> accountCounter = PbtImageProgress.Counter("acc", () => (ulong)Interlocked.Read(ref accounts));
            Func<string> slotCounter = PbtImageProgress.Counter("slot", () => (ulong)Interlocked.Read(ref slots));
            progress.SetFormat(_ => PbtImageProgress.Format(CopyPhase, Volatile.Read(ref donePartitions) / (float)partitionCount,
                $"{accountCounter()} | {slotCounter()}"));
            progress.Reset(0, 0);

            using PeriodicTimer timer = new(CopyLogInterval);
            while (await timer.WaitForNextTickAsync(loggingToken))
            {
                progress.Update((ulong)(Interlocked.Read(ref accounts) + Interlocked.Read(ref slots)));
                progress.LogProgress();
            }
        }

        ITunableDb? tunableDb = pbtDb as ITunableDb;
        tunableDb?.Tune(ITunableDb.TuneType.DisableCompaction);

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

        tunableDb?.Tune(ITunableDb.TuneType.Default);
        if (_logger.IsInfo) _logger.Info("Compacting the PBT database after the flat copy. This may take a while.");
        pbtDb.Compact();
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
            Address address = new(accountKey.Bytes[..Address.Size]);

            Account account = AccountDecoder.Slim.Decode(accountIterator.CurrentValue)!;
            PbtAccount stem = StageStem(account, address, out CodeInfo? code);

            if (account.HasStorage) CopySlots(reader, batch, accountKey, address, ref slots, cancellationToken);

            batch.NextWrite().SetAccount(PbtStateKey.AddressKeyHash(address), stem);
            if (code is not null) batch.NextWrite().SetCode(account.CodeHash.ValueHash256, code);

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
    /// <remarks>Ascending slots let the key deriver reuse one address hash and one suffix hash per 256-slot run, and complete each slot run before the next one starts.</remarks>
    private static void CopySlots(
        FlatPersistence.IPersistenceReader reader,
        CopyBatch batch,
        in ValueHash256 accountKey,
        Address address,
        ref long slots,
        CancellationToken cancellationToken)
    {
        long pendingSlots = 0;
        PbtStorageTreeKey runKey = default;
        PackedSlotRun run = SlotRun.Empty;
        using FlatPersistence.IFlatIterator slotIterator = reader.CreateStorageIterator(accountKey, default, ValueKeccak.MaxValue);
        while (slotIterator.MoveNext())
        {
            cancellationToken.ThrowIfCancellationRequested();

            // In preimage mode, the key is the raw 32-byte big-endian slot.
            UInt256 slot = new(slotIterator.CurrentKey.Bytes, isBigEndian: true);
            EvmWord value = EvmWordSlot.FromStripped(slotIterator.CurrentValue);
            PbtStorageTreeKey key = PbtStateKey.Storage(address, slot);
            PbtStorageTreeKey slotRunKey = SlotRun.RunKey(key);
            if (slotRunKey != runKey)
            {
                Flush();
                runKey = slotRunKey;
            }
            PackedSlotRun previous = run;
            run = run.With(SlotRun.IndexOf(key), value);
            SlotRun.Return(previous);

            if (++pendingSlots >= ProgressPublishInterval)
            {
                Interlocked.Add(ref slots, pendingSlots);
                pendingSlots = 0;
            }
        }

        Flush();
        Interlocked.Add(ref slots, pendingSlots);

        void Flush()
        {
            if (run.Count == 0) return;
            batch.NextWrite().SetSlotRun(runKey, run);
            SlotRun.Return(run);
            run = SlotRun.Empty;
        }
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

    /// <summary>Derives tree leaves in parallel and commits bounded fold windows.</summary>
    private async Task DeriveAndFold(StateId targetState, int workerCount, int partitionCount, CancellationToken cancellationToken)
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
            int nextPartition = -1;
            ScanProgress scanProgress = new(partitionCount);
            string[] partitionNames = ["accounts/code", "storage"];
            ProgressLogger[] progressLoggers = new ProgressLogger[partitionNames.Length];
            for (int zone = 0; zone < progressLoggers.Length; zone++)
            {
                string partitionName = partitionNames[zone];
                ProgressLogger progress = new($"PBT import phase 2 {partitionName}", logManager);
                int countedZone = zone;
                Func<string> slotCounter = PbtImageProgress.Counter("slot", () => scanProgress.GetSlots(countedZone));
                progress.SetFormat(logger =>
                    PbtImageProgress.Format($"PBT import phase 2 {partitionName}", logger.CurrentValue / (float)PbtKeyspaceProgress.Keyspace, slotCounter()));
                progress.Reset(0, PbtKeyspaceProgress.Keyspace);
                progressLoggers[zone] = progress;
            }

            void LogScanProgress()
            {
                for (int zone = 0; zone < progressLoggers.Length; zone++)
                {
                    progressLoggers[zone].Update(scanProgress.GetScanned(zone));
                    progressLoggers[zone].LogProgress();
                }
            }

            using CancellationTokenSource loggingCts = new();
            Task logging = LogScanProgressPeriodically();
            async Task LogScanProgressPeriodically()
            {
                LogScanProgress();
                using PeriodicTimer timer = new(CopyLogInterval);
                try
                {
                    while (await timer.WaitForNextTickAsync(loggingCts.Token)) LogScanProgress();
                }
                catch (OperationCanceledException) when (loggingCts.IsCancellationRequested)
                {
                    // The producer has stopped; its final counters are logged after the ticker exits.
                }
            }

            Task[] workers = new Task[workerCount];
            for (int worker = 0; worker < workers.Length; worker++)
            {
                workers[worker] = Task.Run(async () =>
                {
                    try
                    {
                        using PbtRebuilder.EntrySink sink = new(entries.Writer, EntryChunkSize, cts.Token);
                        int partition;
                        while ((partition = Interlocked.Increment(ref nextPartition)) < partitionCount * 2)
                        {
                            cts.Token.ThrowIfCancellationRequested();
                            int zone = partition / partitionCount;
                            (byte[] start, byte[] end) = ScanBounds(partition % partitionCount, partitionCount);
                            if (zone == 0) await EmitAccounts(start, end, sink, scanProgress, partition, cts.Token);
                            else await EmitStorage(start, end, sink, scanProgress, partition, cts.Token);
                            scanProgress.Complete(partition);
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
            try
            {
                await Task.WhenAll(workers);
            }
            finally
            {
                entries.Writer.TryComplete(producerFailure?.SourceException);
                await loggingCts.CancelAsync();
                await logging;
                LogScanProgress();
            }
        }
    }

    private sealed class ScanProgress(int partitionCount)
    {
        private readonly PbtKeyspaceProgress[] _scanned = [new(partitionCount), new(partitionCount)];
        private readonly long[] _slots = new long[2];

        public void Publish(int partition, ReadOnlySpan<byte> key) => _scanned[partition / partitionCount].Publish(partition % partitionCount, key);

        public void Complete(int partition) => _scanned[partition / partitionCount].Complete(partition % partitionCount);

        public void AddSlots(int partition, int count) => Interlocked.Add(ref _slots[partition / partitionCount], count);

        public ulong GetSlots(int zone) => (ulong)Interlocked.Read(ref _slots[zone]);

        public ulong GetScanned(int zone) => _scanned[zone].Walked;
    }

    private static (byte[] Start, byte[] End) ScanBounds(int partition, int partitionCount)
    {
        byte[] start = new byte[sizeof(ushort)];
        BinaryPrimitives.WriteUInt16BigEndian(start, (ushort)PbtPrefixPartitions.Start(partition, partitionCount));
        if (partition == partitionCount - 1) return (start, PbtColumnSweep.PastEveryKey());

        byte[] end = new byte[sizeof(ushort)];
        BinaryPrimitives.WriteUInt16BigEndian(end, (ushort)PbtPrefixPartitions.Start(partition + 1, partitionCount));
        return (start, end);
    }

    /// <remarks>
    /// Header slots are emitted with their account because they share its stem and node groups;
    /// <see cref="EmitStorage"/> skips them.
    /// </remarks>
    private async Task EmitAccounts(byte[] cursor, byte[] end, PbtRebuilder.EntrySink sink, ScanProgress progress, int partition, CancellationToken cancellationToken)
    {
        ISortedKeyValueStore accounts = (ISortedKeyValueStore)pbtDb.GetColumnDb(PbtColumns.Accounts);
        ISortedKeyValueStore storage = (ISortedKeyValueStore)pbtDb.GetColumnDb(PbtColumns.Storages);
        IDb codes = pbtDb.GetColumnDb(PbtColumns.Codes);
        using ArrayPoolList<KeyValuePair<ValueHash256, Account>> buffered = new(EntryChunkSize);
        using ArrayPoolList<RebuildEntry> headerSlots = new(PbtKeyDerivation.HeaderStorageOffset);
        while (true)
        {
            byte[]? resumeFrom = null;
            using (ISortedView view = accounts.GetViewBetween(cursor, end))
            {
                while (buffered.Count < EntryChunkSize && view.MoveNext())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    buffered.Add(new(new ValueHash256(view.CurrentKey), PbtAccount.Decode(view.CurrentValue).ToAccount()));
                }
                if (buffered.Count == EntryChunkSize) resumeFrom = PbtColumnSweep.AfterKey(view.CurrentKey);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (resumeFrom is not null) progress.Publish(partition, resumeFrom);
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
                foreach ((PbtPath key, ValueHash256 value) in PbtFlatState.AccountLeaves(addressHash, account, code))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await sink.Add(new RebuildEntry((PbtStorageTreeKey)key, value));
                }

                ReadHeaderSlots(storage, addressHash, headerSlots);
                for (int slot = 0; slot < headerSlots.Count; slot++) await sink.Add(headerSlots[slot]);
                progress.AddSlots(partition, headerSlots.Count);
                headerSlots.Clear();
            }
            buffered.Clear();
            if (resumeFrom is null) return;
            cursor = resumeFrom;
        }
    }

    private async Task EmitStorage(byte[] cursor, byte[] end, PbtRebuilder.EntrySink sink, ScanProgress progress, int partition, CancellationToken cancellationToken)
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
                    if (view.CurrentKey[ValueHash256.MemorySize] == Eip8297KeyDerivation.AccountZone) continue;
                    AddSlots(view.CurrentKey, view.CurrentValue, buffered);
                }
                // A row holds up to a run of slots, so a chunk may overshoot its size.
                if (buffered.Count >= EntryChunkSize) resumeFrom = PbtColumnSweep.AfterKey(view.CurrentKey);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (resumeFrom is not null) progress.Publish(partition, resumeFrom);
            for (int index = 0; index < buffered.Count; index++) await sink.Add(buffered[index]);
            progress.AddSlots(partition, buffered.Count);
            buffered.Clear();
            if (resumeFrom is null) return;
            cursor = resumeFrom;
        }
    }

    /// <summary>Adds the staged header-region slots of the account keyed by <paramref name="addressHash"/>.</summary>
    private static void ReadHeaderSlots(ISortedKeyValueStore storage, in ValueHash256 addressHash, ArrayPoolList<RebuildEntry> destination)
    {
        Span<byte> lower = stackalloc byte[ValueHash256.MemorySize + 1];
        addressHash.Bytes.CopyTo(lower);
        lower[^1] = Eip8297KeyDerivation.AccountZone;
        Span<byte> upper = stackalloc byte[ValueHash256.MemorySize + 1];
        lower.CopyTo(upper);
        upper[^1]++;
        using ISortedView view = storage.GetViewBetween(lower, upper);
        while (view.MoveNext()) AddSlots(view.CurrentKey, view.CurrentValue, destination);
    }

    private static void AddSlots(ReadOnlySpan<byte> persistedRunKey, ReadOnlySpan<byte> encodedRun, ArrayPoolList<RebuildEntry> destination)
    {
        PbtStorageTreeKey runKey = PbtStorageKeyLayout.Decode(persistedRunKey);
        PackedSlotRun run = SlotRunCodec.Decode(encodedRun);
        for (int index = 0; index < SlotRun.Width; index++)
        {
            if ((run.Mask & (1 << index)) == 0) continue;
            EvmWord slot = run.Get(index);
            destination.Add(new(SlotRun.SlotKey(runKey, index), new ValueHash256(EvmWordSlot.AsReadOnlySpan(in slot))));
        }
        SlotRun.Return(run);
    }

    /// <summary>The stem of <paramref name="account"/>, with the code to stage when this call is the first to see its code hash.</summary>
    private PbtAccount StageStem(Account account, Address address, out CodeInfo? code)
    {
        code = null;
        if (!account.HasCode) return PbtAccount.From(account, null);
        ValueHash256 codeHash = account.CodeHash.ValueHash256;
        // Workers meeting one code hash wait for its first stager, so shared bytecode is fetched once.
        lock (_codeLocks[codeHash.Bytes[0]])
        {
            if (_stagedCodes.TryGet(codeHash, out PbtAccount staged)) return staged.WithNonceAndBalance(account);
            code = new CodeInfo(codeDb.Get(codeHash.Bytes)
                ?? throw new InvalidDataException($"Missing bytecode for {address} (code hash {account.CodeHash}) in the code database."));
            PbtAccount stem = PbtAccount.From(account, code);
            _stagedCodes.Set(codeHash, stem);
            return stem;
        }
    }

}
