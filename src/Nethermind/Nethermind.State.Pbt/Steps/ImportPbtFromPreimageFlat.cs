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
using FlatDbColumns = Nethermind.State.Flat.FlatDbColumns;
using FlatPersistence = Nethermind.State.Flat.Persistence.IPersistence;
using FlatSlotValue = Nethermind.State.Flat.SlotValue;
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
/// The import runs in two phases, and the PBT's own flat columns are what carries state between them:
/// first the source is copied into the PBT flat account and storage columns, in parallel over ranges of
/// the source's account key space; then those columns are scanned back to derive the tree leaves, which
/// the flat key layout hands over in stem order so nothing has to sort them.
/// The phases cannot overlap: phase one partitions the source by address, which scatters across the
/// whole address-hash space, so no range of phase two's key space is complete until all of phase one is.
/// </remarks>
[RunnerStepDependencies(
    dependencies: [typeof(InitializeBlockTree)],
    dependents: [typeof(InitializeBlockchain)]
)]
public class ImportPbtFromPreimageFlat(
    FlatPersistence flatSource,
    IColumnsDb<FlatDbColumns> flatSourceDb,
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

    /// <summary>Address bytes a flat storage key carries up front; the rest trail the slot. See <see cref="CopySlots"/>.</summary>
    private const int FlatKeyAddressPrefix = 4;

    /// <summary>Slot bytes of a flat storage key, holding the raw slot number in preimage mode.</summary>
    private const int FlatKeySlotLength = 32;

    /// <summary>A flat storage key carries a whole address, split around the slot.</summary>
    private const int FlatStorageKeyLength = AddressLength + FlatKeySlotLength;

    /// <summary>A tree key is a stem plus its sub-index byte, and is also a flat storage key.</summary>
    private const int TreeKeyLength = Stem.Length + 1;

    /// <summary>Bit position of a stem's zone within its first byte.</summary>
    private const int ZoneShift = 4;

    /// <summary>First byte of the lowest storage-zone stem; the zone is the top bit rather than a nibble value.</summary>
    private const byte StorageZoneFirstByte = 0x80;

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
            ClearInterruptedAttempt();
            await CopyFlatColumns(workerCount, cancellationToken);
            await DeriveAndFold(sourceState.BlockNumber, cancellationToken);
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
    /// have written flat rows, leaf blobs and trie nodes, and those are not inert:
    /// <see cref="TrieUpdater"/> reads the stored root group before it consults the root hash it was
    /// handed, so a rebuild starting from an empty root folds against the stale tree and settles on the
    /// wrong root. Metadata is left alone — it carries the layout version, and its state pointer is
    /// pre-genesis already.
    /// </remarks>
    private void ClearInterruptedAttempt()
    {
        // delete in bounded batches; one batch over every key exhausts memory on a large target
        const int batchSize = 10_000;

        long cleared = 0;
        IColumnsWriteBatch<PbtColumns> batch = pbtDb.StartWriteBatch();
        try
        {
            int count = 0;
            foreach (PbtColumns column in Enum.GetValues<PbtColumns>())
            {
                if (column == PbtColumns.Metadata) continue;

                foreach (byte[] key in pbtDb.GetColumnDb(column).GetAllKeys())
                {
                    batch.GetColumnBatch(column).Remove(key);
                    cleared++;
                    if (++count == batchSize)
                    {
                        IColumnsWriteBatch<PbtColumns> next = pbtDb.StartWriteBatch();
                        batch.Dispose(); // commit the chunk
                        batch = next;
                        count = 0;
                    }
                }
            }
        }
        finally
        {
            batch.Dispose();
        }

        if (cleared > 0 && _logger.IsInfo) _logger.Info($"Discarded {cleared:N0} entries left by an interrupted PBT import.");
    }

    /// <summary>
    /// Phase one: copies the source's accounts and slots into the PBT flat columns, re-keyed on the
    /// address key hash and the slot tree key respectively.
    /// </summary>
    /// <remarks>
    /// Each worker owns a source reader, a PBT write batch and the range it is currently on, so nothing
    /// is shared but the counters. Ranges are handed out on demand rather than assigned up front
    /// because storage sizes are wildly uneven. Every batch is written
    /// <see cref="StateId.PreGenesis"/> → <see cref="StateId.PreGenesis"/> with
    /// <see cref="WriteFlags.DisableWAL"/>: the persisted-state pointer stays pre-genesis until the
    /// rebuild completes, so a crash mid-import leaves a state that the next run simply overwrites —
    /// the flat keys are a deterministic function of the source, so re-copying is idempotent.
    /// </remarks>
    private async Task CopyFlatColumns(int workerCount, CancellationToken cancellationToken)
    {
        Stopwatch copying = Stopwatch.StartNew();
        int partitionCount = Math.Min(workerCount * PartitionsPerWorker, PartitionPrefixSpace);

        int nextPartition = -1, donePartitions = 0;
        long accounts = 0, slots = 0;

        ISortedKeyValueStore sourceStorage = (ISortedKeyValueStore)flatSourceDb.GetColumnDb(FlatDbColumns.Storage);
        bool rlpWrapSlots;
        using (FlatPersistence.IPersistenceReader probe = flatSource.CreateReader())
        {
            rlpWrapSlots = DetectRlpWrappedSlots(probe, sourceStorage);
        }

        void CopyPartitions()
        {
            using FlatPersistence.IPersistenceReader reader = flatSource.CreateReader();

            int partition;
            while ((partition = Interlocked.Increment(ref nextPartition)) < partitionCount)
            {
                (ValueHash256 start, ValueHash256 end) = PartitionBounds(partition, partitionCount);

                // one batch per range rather than per worker: it bounds how much a batch buffers, and
                // the persisted-state pointer it rewrites is the same pre-genesis value either way
                using (IPbtPersistence.IWriteBatch batch = pbtPersistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.DisableWAL))
                {
                    CopyAccounts(reader, batch, start, end, ref accounts, cancellationToken);
                    CopySlots(sourceStorage, batch, rlpWrapSlots, partition, partitionCount, ref slots, cancellationToken);
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

    private static void CopyAccounts(
        FlatPersistence.IPersistenceReader reader,
        IPbtPersistence.IWriteBatch batch,
        ValueHash256 start,
        ValueHash256 end,
        ref long accounts,
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

            batch.SetAccount(address, DecodeAccount(accountIterator.CurrentValue));
            pendingAccounts++;

            if (pendingAccounts >= ProgressPublishInterval)
            {
                Interlocked.Add(ref accounts, pendingAccounts);
                pendingAccounts = 0;
            }
        }

        Interlocked.Add(ref accounts, pendingAccounts);
    }

    /// <summary>
    /// Copies one partition's slots by sweeping the source's storage column, taking each slot's address
    /// from the key rather than seeking per account.
    /// </summary>
    /// <remarks>
    /// A flat storage key is <c>address[0..4] | slot | address[4..20]</c>: the address is split so
    /// RocksDB's comparator can skip its tail and shorten the index. That leaves every account sharing
    /// the leading four address bytes interleaved under them, ordered by slot, so iterating one
    /// account's storage has to walk the whole four-byte group and discard its neighbours' rows. On
    /// hashed keys a group holds one account and that costs nothing, but preimage keys are raw
    /// addresses: mined vanity addresses cluster under <c>0x00000000</c> alongside some of the largest
    /// storage contracts on the chain, so each one rescans all of their slots and the partition holding
    /// them never finishes. Sweeping the column once turns that from O(accounts × slots in group) into a
    /// single ordered pass.
    /// <para>
    /// Slots still reach the batch grouped by address and in ascending slot order wherever a group holds
    /// one account — the ordering its key deriver needs to charge one address hash per account plus one
    /// suffix hash per 256-slot run. In a shared group the deriver falls back to a hash per slot.
    /// </para>
    /// </remarks>
    private static void CopySlots(
        ISortedKeyValueStore sourceStorage,
        IPbtPersistence.IWriteBatch batch,
        bool rlpWrapSlots,
        int partition,
        int partitionCount,
        ref long slots,
        CancellationToken cancellationToken)
    {
        (byte[] start, byte[] end) = StoragePartitionBounds(partition, partitionCount);

        long pendingSlots = 0;
        Span<byte> addressBytes = stackalloc byte[AddressLength];
        Address? address = null;

        using ISortedView view = sourceStorage.GetViewBetween(start, end);
        while (view.MoveNext())
        {
            cancellationToken.ThrowIfCancellationRequested();

            ReadOnlySpan<byte> key = view.CurrentKey;
            if (key.Length != FlatStorageKeyLength) continue;

            ReadOnlySpan<byte> addressHead = key[..FlatKeyAddressPrefix];
            ReadOnlySpan<byte> addressTail = key[(FlatKeyAddressPrefix + FlatKeySlotLength)..];
            if (address is null
                || !addressHead.SequenceEqual(addressBytes[..FlatKeyAddressPrefix])
                || !addressTail.SequenceEqual(addressBytes[FlatKeyAddressPrefix..]))
            {
                addressHead.CopyTo(addressBytes);
                addressTail.CopyTo(addressBytes[FlatKeyAddressPrefix..]);
                address = new Address(addressBytes);
            }

            // preimage mode: the flat slot key holds the raw slot as a 32-byte big-endian value
            UInt256 slot = new(key.Slice(FlatKeyAddressPrefix, FlatKeySlotLength), isBigEndian: true);
            ReadOnlySpan<byte> stored = rlpWrapSlots ? new RlpReader(view.CurrentValue).DecodeByteArraySpan() : view.CurrentValue;
            batch.SetSlot(address, slot, EvmWordSlot.FromStripped(stored));

            if (++pendingSlots >= ProgressPublishInterval)
            {
                Interlocked.Add(ref slots, pendingSlots);
                pendingSlots = 0;
            }
        }

        Interlocked.Add(ref slots, pendingSlots);
    }

    /// <summary>
    /// Whether the source stores slot values RLP-wrapped, decided by re-reading one row through the
    /// source's own reader and comparing.
    /// </summary>
    /// <remarks>
    /// The encoding is recorded in flat metadata that this assembly cannot read, and the two encodings
    /// overlap — RLP leaves a lone byte below <c>0x80</c> untouched — so a row is only conclusive when
    /// its stored value is not one of those. Rows that are not conclusive are skipped; a source with no
    /// conclusive row decodes the same either way.
    /// </remarks>
    private static bool DetectRlpWrappedSlots(FlatPersistence.IPersistenceReader reader, ISortedKeyValueStore sourceStorage)
    {
        using ISortedView view = sourceStorage.GetViewBetween(new byte[FlatStorageKeyLength], PastEveryStorageKey());
        while (view.MoveNext())
        {
            ReadOnlySpan<byte> key = view.CurrentKey;
            ReadOnlySpan<byte> stored = view.CurrentValue;
            if (key.Length != FlatStorageKeyLength || (stored.Length == 1 && stored[0] < 0x80)) continue;

            ValueHash256 accountKey = default;
            key[..FlatKeyAddressPrefix].CopyTo(accountKey.BytesAsSpan);
            key[(FlatKeyAddressPrefix + FlatKeySlotLength)..].CopyTo(accountKey.BytesAsSpan[FlatKeyAddressPrefix..]);
            ValueHash256 slotKey = new(key.Slice(FlatKeyAddressPrefix, FlatKeySlotLength));

            FlatSlotValue decoded = default;
            if (!reader.TryGetStorageRaw(accountKey, slotKey, ref decoded)) continue;

            EvmWord asRaw = EvmWordSlot.FromStripped(stored);
            return !EvmWordSlot.AsReadOnlySpan(in asRaw).SequenceEqual(decoded.AsReadOnlySpan);
        }

        return true;
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

    /// <summary>The storage-column key range of one copy partition, covering exactly <see cref="PartitionBounds"/>'s accounts.</summary>
    /// <remarks>
    /// A storage key opens with the address's first four bytes, so the two bytes the partitions are cut
    /// on lead the key as well and the range needs no filtering.
    /// </remarks>
    private static (byte[] Start, byte[] End) StoragePartitionBounds(int partition, int partitionCount)
    {
        byte[] start = new byte[FlatStorageKeyLength];
        BinaryPrimitives.WriteUInt16BigEndian(start, (ushort)((long)partition * PartitionPrefixSpace / partitionCount));

        // the last partition runs past every key; its own upper bound would cut the 0xFFFF group short
        if (partition == partitionCount - 1) return (start, PastEveryStorageKey());

        byte[] end = new byte[FlatStorageKeyLength];
        BinaryPrimitives.WriteUInt16BigEndian(end, (ushort)((long)(partition + 1) * PartitionPrefixSpace / partitionCount));
        return (start, end);
    }

    /// <summary>One byte longer than any flat storage key, so it sorts above every one of them.</summary>
    private static byte[] PastEveryStorageKey()
    {
        byte[] key = new byte[FlatStorageKeyLength + 1];
        key.AsSpan().Fill(0xFF);
        return key;
    }

    /// <summary>
    /// Phase two: derives the tree leaves from the PBT flat columns and folds them.
    /// The scan is single-threaded because the fold it feeds is, and because the rebuilder's windowing
    /// only pays off while the leaves stay ordered.
    /// </summary>
    private async Task DeriveAndFold(ulong blockNumber, CancellationToken cancellationToken)
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
            await rebuilder.Rebuild(entries.Reader, blockNumber, cancellationToken);
        }
        finally
        {
            await cts.CancelAsync();
            try { await producer; }
            catch { /* the failure already surfaced through the consumer above */ }
        }
    }

    /// <summary>
    /// Emits every tree leaf, in ascending tree-key order, by zone: the account headers, then the
    /// content-addressed overflow code chunks, then the storage slots.
    /// </summary>
    /// <remarks>
    /// The zones are disjoint subtrees and sort in that order (a stem's top nibble is its zone: 0x0
    /// accounts, 0x1 code, 0x8 storage), so emitting them one after another yields a globally ascending
    /// stream. The account and storage zones come straight off an ordered column scan; only the code
    /// zone has to be sorted here, because an overflow stem is derived from the code hash and so has no
    /// relation to the order accounts are scanned in.
    /// </remarks>
    private async Task ProduceEntries(ChannelWriter<ArrayPoolList<RebuildEntry>> entries, CancellationToken cancellationToken)
    {
        using LeafSink sink = new(entries, cancellationToken);
        try
        {
            using IColumnDbSnapshot<PbtColumns> snapshot = pbtDb.CreateSnapshot();
            ISortedKeyValueStore accountColumn = (ISortedKeyValueStore)snapshot.GetColumn(PbtColumns.Account);
            ISortedKeyValueStore storageColumn = (ISortedKeyValueStore)snapshot.GetColumn(PbtColumns.Storage);

            // code hash -> chunk count, for the codes that spill past the account header
            Dictionary<ValueHash256, int> overflowCodes = [];

            await EmitAccountZone(accountColumn, storageColumn, overflowCodes, sink, cancellationToken);
            await EmitCodeZone(overflowCodes, sink, cancellationToken);
            await EmitStorageZone(storageColumn, sink, cancellationToken);

            await sink.Complete();
            entries.TryComplete();
        }
        catch (Exception e)
        {
            entries.TryComplete(e);
        }
    }

    /// <summary>
    /// Emits the account zone: every account's <c>BASIC_DATA</c>, <c>CODE_HASH</c>, header storage
    /// slots and header code chunks, all of which share that account's header stem.
    /// </summary>
    /// <remarks>
    /// The account column is keyed by the address hash and the header stem is that hash's top 244 bits
    /// behind a zero zone nibble, so the column already enumerates in stem order. Header slots
    /// (<c>slot &lt; 64</c>) live on the same stem, which puts them in the storage column's zone-0
    /// range in the very same order — so the two are merge-joined rather than seeked per account.
    /// </remarks>
    private async Task EmitAccountZone(
        ISortedKeyValueStore accountColumn,
        ISortedKeyValueStore storageColumn,
        Dictionary<ValueHash256, int> overflowCodes,
        LeafSink sink,
        CancellationToken cancellationToken)
    {
        using ISortedView accounts = accountColumn.GetViewBetween(FirstKey(), PastEveryKey());
        using ISortedView headerSlots = storageColumn.GetViewBetween(FirstKey(), ZoneFirstKey(PbtKeyDerivation.CodeZone << ZoneShift));
        bool hasSlot = headerSlots.MoveNext();
        long orphanSlots = 0;

        while (accounts.MoveNext())
        {
            cancellationToken.ThrowIfCancellationRequested();

            ValueHash256 addressHash = new(accounts.CurrentKey);
            Account account = DecodeAccount(accounts.CurrentValue);
            Stem headerStem = PbtKeyDerivation.AccountHeaderStem(addressHash);

            byte[]? code = account.HasCode
                ? codeDb.Get(account.CodeHash.Bytes) ?? throw new InvalidDataException($"Missing bytecode for the account hashing to {addressHash} (code hash {account.CodeHash}) in the code database.")
                : null;

            ValueHash256 basicData = default;
            PbtKeyDerivation.PackBasicData(basicData.BytesAsSpan, code is null ? 0u : (uint)code.Length, account.Nonce, account.Balance);

            await sink.Add(new RebuildEntry(headerStem, PbtKeyDerivation.BasicDataLeafKey, basicData));
            await sink.Add(new RebuildEntry(headerStem, PbtKeyDerivation.CodeHashLeafKey, account.CodeHash.ValueHash256));

            // a zone-0 storage row below this account's stem belongs to no account in the column; it
            // cannot happen, but skipping it silently would hide a corrupt copy
            while (hasSlot && CompareStem(headerSlots.CurrentKey, headerStem) < 0)
            {
                orphanSlots++;
                hasSlot = headerSlots.MoveNext();
            }

            while (hasSlot && CompareStem(headerSlots.CurrentKey, headerStem) == 0)
            {
                byte subIndex = headerSlots.CurrentKey[Stem.Length];
                ValueHash256 leaf = SlotLeaf(headerSlots.CurrentValue);
                await sink.Add(new RebuildEntry(headerStem, subIndex, leaf));
                hasSlot = headerSlots.MoveNext();
            }

            if (code is not { Length: > 0 }) continue;

            byte[] chunks = PbtKeyDerivation.ChunkifyCode(code);
            int chunkCount = chunks.Length / PbtKeyDerivation.CodeChunkSize;
            int headerChunks = Math.Min(chunkCount, PbtKeyDerivation.HeaderCodeChunks);
            for (int i = 0; i < headerChunks; i++)
            {
                await sink.Add(new RebuildEntry(headerStem, PbtKeyDerivation.HeaderCodeChunkSubIndex(i), ChunkLeaf(chunks, i)));
            }

            // the overflow chunks are content-addressed, so one copy serves every account sharing this
            // code; they are emitted together in the code zone once this zone is done
            if (chunkCount > PbtKeyDerivation.HeaderCodeChunks) overflowCodes[account.CodeHash.ValueHash256] = chunkCount;
        }

        if (orphanSlots > 0 && _logger.IsWarn) _logger.Warn($"PBT import found {orphanSlots:N0} account-zone storage rows with no matching account; they were skipped.");
    }

    /// <summary>Emits the code zone: the overflow chunks of every code too long to fit the account header.</summary>
    /// <remarks>
    /// An overflow stem is a hash of the code hash, so it bears no relation to the order the accounts
    /// referencing it were scanned in. The runs are therefore collected and ordered here, which is what
    /// keeps this zone one contiguous sweep rather than a scatter across every window. The list holds
    /// one entry per stem-sized run — EIP-170 caps code at 24576 bytes, so at most three per code.
    /// </remarks>
    private async Task EmitCodeZone(Dictionary<ValueHash256, int> overflowCodes, LeafSink sink, CancellationToken cancellationToken)
    {
        using ArrayPoolList<CodeChunkRun> runs = new(overflowCodes.Count);
        foreach ((ValueHash256 codeHash, int chunkCount) in overflowCodes)
        {
            for (int i = PbtKeyDerivation.HeaderCodeChunks; i < chunkCount;)
            {
                Stem stem = PbtKeyDerivation.CodeOverflowStem(codeHash, i, out byte subIndex);
                int length = Math.Min(chunkCount - i, PbtKeyDerivation.StemSubtreeWidth - subIndex);
                runs.Add(new CodeChunkRun(stem, codeHash, i, subIndex, length));
                i += length;
            }
        }

        runs.Sort(static (a, b) => a.Stem.Bytes.SequenceCompareTo(b.Stem.Bytes));

        // one-entry memo: a code's runs only land adjacent when the sort happens to put them there
        byte[]? chunks = null;
        ValueHash256 chunkedCode = default;

        for (int r = 0; r < runs.Count; r++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            CodeChunkRun run = runs[r];
            if (chunks is null || chunkedCode != run.CodeHash)
            {
                chunks = PbtKeyDerivation.ChunkifyCode(codeDb.Get(run.CodeHash.Bytes) ?? throw new InvalidDataException($"Missing bytecode for code hash {run.CodeHash} in the code database."));
                chunkedCode = run.CodeHash;
            }

            for (int j = 0; j < run.Length; j++)
            {
                await sink.Add(new RebuildEntry(run.Stem, (byte)(run.FirstSubIndex + j), ChunkLeaf(chunks, run.FirstChunkId + j)));
            }
        }
    }

    /// <summary>Emits the storage zone straight off the column, whose keys already are the tree keys.</summary>
    private async Task EmitStorageZone(ISortedKeyValueStore storageColumn, LeafSink sink, CancellationToken cancellationToken)
    {
        using ISortedView view = storageColumn.GetViewBetween(ZoneFirstKey(StorageZoneFirstByte), PastEveryKey());
        while (view.MoveNext())
        {
            cancellationToken.ThrowIfCancellationRequested();

            Stem stem = new(view.CurrentKey[..Stem.Length]);
            byte subIndex = view.CurrentKey[Stem.Length];
            ValueHash256 leaf = SlotLeaf(view.CurrentValue);
            await sink.Add(new RebuildEntry(stem, subIndex, leaf));
        }
    }

    /// <summary>One stem's worth of a code's overflow chunks, so the runs can be ordered before they are emitted.</summary>
    private readonly record struct CodeChunkRun(Stem Stem, ValueHash256 CodeHash, int FirstChunkId, byte FirstSubIndex, int Length);

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

    /// <summary>A slot's tree leaf: the stored value re-padded to the canonical 32 bytes.</summary>
    private static ValueHash256 SlotLeaf(ReadOnlySpan<byte> stored)
    {
        EvmWord word = EvmWordSlot.FromStripped(stored);
        return new ValueHash256(EvmWordSlot.AsReadOnlySpan(in word));
    }

    private static ValueHash256 ChunkLeaf(byte[] chunks, int chunkId) =>
        new(chunks.AsSpan(chunkId * PbtKeyDerivation.CodeChunkSize, PbtKeyDerivation.CodeChunkSize));

    /// <summary>Orders a tree key against a stem, ignoring the key's trailing sub-index byte.</summary>
    private static int CompareStem(ReadOnlySpan<byte> treeKey, in Stem stem) =>
        treeKey[..Stem.Length].SequenceCompareTo(stem.Bytes);

    private static byte[] FirstKey() => new byte[TreeKeyLength];

    private static byte[] ZoneFirstKey(byte firstByte)
    {
        byte[] key = new byte[TreeKeyLength];
        key[0] = firstByte;
        return key;
    }

    /// <summary>One byte longer than any tree key, so it sorts above every one of them.</summary>
    private static byte[] PastEveryKey()
    {
        byte[] key = new byte[TreeKeyLength + 1];
        key.AsSpan().Fill(0xFF);
        return key;
    }
}
