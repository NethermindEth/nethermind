// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Buffers;
using Nethermind.Db;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Pbt;
using Nethermind.Serialization.Rlp;
using FlatPersistence = Nethermind.State.Flat.Persistence.IPersistence;

namespace Nethermind.State.Pbt.Image;

/// <summary>Exports a caller-pinned offline preimage-flat generation into unpublished canonical artifacts.</summary>
/// <remarks>The caller owns the reader, code source and outputs and must hold the source exclusively immutable
/// throughout this operation. This helper never opens or writes a source database. Outputs must pass
/// <see cref="PbtImageVerifier"/> before publication: flat metadata alone does not prove the anchor root.
/// <para>The scan workers share the one reader: its iterators are independent, and the source is pinned for the
/// whole call anyway, so a reader per worker would only multiply the pinned snapshots.</para></remarks>
internal static class PbtOfflineSource
{
    /// <summary>Keccak address path, the account/slot tag, then the Keccak slot path.</summary>
    private const int PreimageKeyLength = 65;

    /// <summary>Address ranges per worker, so an uneven spread of storage still balances out.</summary>
    private const int PartitionsPerWorker = 16;

    /// <summary>Distinct values of the two leading address bytes the partitions are cut on.</summary>
    private const int PartitionPrefixSpace = 1 << 16;

    /// <summary>Records a worker counts privately before publishing them to the shared progress counters.</summary>
    private const int ProgressPublishInterval = 100_000;

    private const string ScanPhase = "PBT export scan";

    /// <param name="sortBufferBytes">Sort budget per worker, split between the leaf and preimage spools.</param>
    /// <param name="workerCount">Scan workers; zero uses the processor count.</param>
    public static PbtArtifactWriter.PbtArtifactDigests WriteArtifacts(FlatPersistence.IPersistenceReader source,
        IReadOnlyKeyValueStore codeSource, PbtImageAnchor anchor, string scratchDirectory,
        Stream snapshot, Stream preimages, ILogManager logManager,
        int sortBufferBytes, int workerCount, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sortBufferBytes, 1024);
        ArgumentOutOfRangeException.ThrowIfNegative(workerCount);
        ArgumentOutOfRangeException.ThrowIfNegative(anchor.MaxBufferedCodeBytes);
        cancellationToken.ThrowIfCancellationRequested();
        if (!source.IsPreimageMode || source.CurrentState.BlockNumber != anchor.Header.Number ||
            anchor.Header.StateRoot is null || source.CurrentState.StateRoot != anchor.Header.StateRoot.ValueHash256 ||
            anchor.ActivationTimestamp is { } activation && anchor.Header.Timestamp >= activation || anchor.Header.Hash is null)
            throw new InvalidDataException("Offline source does not match the trusted pre-activation anchor.");
        int workers = workerCount > 0 ? workerCount : Environment.ProcessorCount;
        ILogger logger = logManager.GetClassLogger(typeof(PbtOfflineSource));
        Stopwatch exporting = Stopwatch.StartNew();
        string directory = Path.Combine(scratchDirectory, $"pbt-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            using PbtSortedSpool leaves = new(directory, sortBufferBytes / 2, workers, logManager, cancellationToken);
            using PbtSortedSpool rawKeys = new(directory, sortBufferBytes / 2, workers, logManager, cancellationToken);
            ScanTotals totals = new();
            Scan();

            ulong leafCount = 0;
            ulong accountCount = (ulong)totals.Accounts;
            ValueHash256 root = PbtImageRootCalculator.Calculate(CountLeaves(), cancellationToken);
            PbtArtifactWriter.PbtArtifactDigests digests = PbtArtifactWriter.Write(snapshot, preimages, root, leafCount,
                Leaves("PBT export snapshot", leafCount), Accounts(), cancellationToken);
            if (logger.IsInfo)
                logger.Info($"PBT export wrote {leafCount:N0} leaves for {accountCount:N0} accounts and {totals.Slots:N0} slots in {exporting.Elapsed:hh\\:mm\\:ss}.");
            return digests;

            // Workers claim address ranges on demand; the spools restore the total order the walk does not have.
            void Scan()
            {
                int partitionCount = Math.Min(workers * PartitionsPerWorker, PartitionPrefixSpace);
                int nextPartition = -1;
                using ProgressReporter progress = PbtImageProgress.Start(ScanPhase, "acc", 0, logManager);
                progress.Logger.SetFormat(p =>
                    $"{PbtImageProgress.Format(ScanPhase, "acc", p)} | {Interlocked.Read(ref totals.Slots),15:N0} slot");

                void ScanPartitions()
                {
                    using PbtSortedSpool.Writer leafWriter = leaves.CreateWriter();
                    using PbtSortedSpool.Writer rawKeyWriter = rawKeys.CreateWriter();
                    ScanWorker worker = new(codeSource, anchor, leafWriter, rawKeyWriter, progress, totals);
                    int partition;
                    while ((partition = Interlocked.Increment(ref nextPartition)) < partitionCount)
                    {
                        (ValueHash256 start, ValueHash256 end) = PartitionBounds(partition, partitionCount);
                        worker.ScanRange(source, start, end, cancellationToken);
                        // The flat iterator's upper bound is exclusive and truncates to twenty bytes, so the
                        // maximum address is reached only by the last range, and only by an explicit lookup.
                        if (partition == partitionCount - 1) worker.ScanMaximumAddress(source);
                    }
                    worker.PublishProgress();
                }

                Task[] running = new Task[workers];
                for (int index = 0; index < workers; index++)
                    // Dedicated threads: a worker blocked on the spool's segment pool must not hold a pool thread
                    // that one of the sorts freeing that segment is queued behind.
                    running[index] = Task.Factory.StartNew(ScanPartitions, cancellationToken,
                        TaskCreationOptions.LongRunning, TaskScheduler.Default);

                // Joined without the token: the workers observe it themselves, and abandoning one of them here
                // would leave it writing into spools the unwinding caller is about to dispose.
                try
                {
                    Task.WaitAll(running, CancellationToken.None);
                }
                catch (AggregateException failures)
                {
                    ExceptionDispatchInfo.Capture(failures.Flatten().InnerExceptions[0]).Throw();
                }
            }

            IEnumerable<RebuildEntry> CountLeaves()
            {
                foreach (RebuildEntry entry in Leaves("PBT export hash", 0))
                {
                    leafCount++;
                    yield return entry;
                }
            }

            // The spool is drained once to hash and once to write, so each drain names its own phase.
            IEnumerable<RebuildEntry> Leaves(string phase, ulong total)
            {
                ulong drained = 0;
                using ProgressReporter progress = PbtImageProgress.Start(phase, "leaf", total, logManager);
                using PbtSortedSpool.Cursor cursor = leaves.Read();
                while (cursor.MoveNext())
                {
                    progress.Update(++drained);
                    yield return new RebuildEntry(new PbtStorageTreeKey(cursor.Key), new ValueHash256(cursor.Value));
                }
            }

            IEnumerable<PbtAccountPreimages> Accounts()
            {
                ulong written = 0;
                using ProgressReporter progress = PbtImageProgress.Start("PBT export preimages", "acc", accountCount, logManager);
                using PbtSortedSpool.Cursor cursor = rawKeys.Read();
                while (cursor.MoveNext())
                {
                    progress.Update(++written);
                    if (cursor.Key[32] != 0) throw new InvalidDataException("Orphan source slot.");
                    ValueHash256 accountHash = new(cursor.Key[..32]);
                    uint count = BinaryPrimitives.ReadUInt32BigEndian(cursor.Value[Address.Size..]);
                    yield return new(new Address(cursor.Value[..Address.Size]), count, Slots());
                    IEnumerable<ValueHash256> Slots()
                    {
                        for (uint index = 0; index < count; index++)
                        {
                            if (!cursor.MoveNext() || cursor.Key[32] != 1 || !cursor.Key[..32].SequenceEqual(accountHash.Bytes))
                                throw new InvalidDataException("Source slot count mismatch.");
                            yield return new ValueHash256(cursor.Value);
                        }
                    }
                }
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
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

    /// <summary>The scan's shared record counts, published by the workers and read by the progress format.</summary>
    private sealed class ScanTotals
    {
        public long Accounts;
        public long Slots;
    }

    /// <summary>One scan worker: its own spool writers, scratch keys and unpublished progress counts.</summary>
    /// <remarks>The scratch keys belong to the worker rather than to the scan, so two accounts can never
    /// interleave through one preimage key and corrupt the preimage stream.</remarks>
    private sealed class ScanWorker(
        IReadOnlyKeyValueStore codeSource,
        PbtImageAnchor anchor,
        PbtSortedSpool.Writer leaves,
        PbtSortedSpool.Writer rawKeys,
        ProgressReporter progress,
        ScanTotals totals)
    {
        private readonly byte[] _preimageKey = new byte[PreimageKeyLength];
        private readonly byte[] _accountValue = new byte[Address.Size + sizeof(uint)];
        private long _pendingAccounts;
        private long _pendingSlots;

        public void ScanRange(FlatPersistence.IPersistenceReader reader, in ValueHash256 start, in ValueHash256 end,
            CancellationToken cancellationToken)
        {
            using FlatPersistence.IFlatIterator accounts = reader.CreateAccountIterator(start, end);
            while (accounts.MoveNext())
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValueHash256 accountKey = accounts.CurrentKey;
                RlpReader rlp = new(accounts.CurrentValue);
                Account account = AccountDecoder.Slim.Decode(ref rlp) ?? throw new InvalidDataException("Invalid source account.");
                ScanAccount(reader, accountKey, account, cancellationToken);
            }
        }

        /// <remarks>The flat iterator cannot reach the maximum address, so it is fetched by key instead.</remarks>
        public void ScanMaximumAddress(FlatPersistence.IPersistenceReader reader)
        {
            ValueHash256 lastKey = default;
            lastKey.BytesAsSpan[..Address.Size].Fill(0xff);
            if (reader.GetAccount(new Address(lastKey.Bytes[..Address.Size])) is { } lastAccount)
                ScanAccount(reader, lastKey, lastAccount, CancellationToken.None);
        }

        private void ScanAccount(FlatPersistence.IPersistenceReader reader, in ValueHash256 accountKey, Account account,
            CancellationToken cancellationToken)
        {
            Address address = new(accountKey.Bytes[..Address.Size]);
            CodeInfo? code = null;
            if (account.HasCode)
            {
                byte[] bytes = codeSource.Get(account.CodeHash.Bytes) ?? throw new InvalidDataException("Missing source code.");
                if ((long)bytes.Length + ((long)bytes.Length + 30) / 31 * 32 > anchor.MaxBufferedCodeBytes)
                    throw new PbtImageResourceLimitException("Source code requires a larger local buffering budget.");
                if (Keccak.Compute(bytes) != account.CodeHash) throw new InvalidDataException("Source code hash mismatch.");
                code = new CodeInfo(bytes);
            }
            ValueHash256 addressKeyHash = PbtKeyDerivation.AddressKeyHash(address);
            foreach ((PbtPath key, ValueHash256 value) in PbtFlatState.AccountLeaves(addressKeyHash, account, code))
                AddLeaf((PbtStorageTreeKey)key, value);

            ValueHash256 addressHash = ValueKeccak.Compute(address.Bytes);
            uint count = 0;
            using (FlatPersistence.IFlatIterator slots = reader.CreateStorageIterator(accountKey, default, ValueKeccak.MaxValue))
            {
                while (slots.MoveNext())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ValueHash256 slot = slots.CurrentKey;
                    EvmWord value = EvmWordSlot.FromStripped(slots.CurrentValue);
                    if (EvmWordSlot.IsZero(value)) throw new InvalidDataException("Source contains a zero storage slot.");
                    AddLeaf(PbtStateKey.Storage(address, new UInt256(slot.Bytes, isBigEndian: true)), new ValueHash256(EvmWordSlot.AsReadOnlySpan(in value)));
                    addressHash.Bytes.CopyTo(_preimageKey);
                    _preimageKey[32] = 1;
                    ValueKeccak.Compute(slot.Bytes).Bytes.CopyTo(_preimageKey.AsSpan(33));
                    rawKeys.Add(_preimageKey, slot.Bytes);
                    count = checked(count + 1);
                    _pendingSlots++;
                }
            }
            // The zero tag and slot-hash region keep an account ahead of its own slots.
            Array.Clear(_preimageKey);
            addressHash.Bytes.CopyTo(_preimageKey);
            address.Bytes.CopyTo(_accountValue);
            BinaryPrimitives.WriteUInt32BigEndian(_accountValue.AsSpan(Address.Size), count);
            rawKeys.Add(_preimageKey, _accountValue);

            _pendingAccounts++;
            if (_pendingAccounts >= ProgressPublishInterval || _pendingSlots >= ProgressPublishInterval) PublishProgress();
        }

        /// <summary>Fold this worker's private counts into the shared ones and report the new total.</summary>
        public void PublishProgress()
        {
            long accounts = Interlocked.Add(ref totals.Accounts, _pendingAccounts);
            Interlocked.Add(ref totals.Slots, _pendingSlots);
            _pendingAccounts = 0;
            _pendingSlots = 0;
            progress.Update((ulong)accounts);
        }

        // Leaf keys are prefix-free (34 bytes in zone 0/1, 66 in zone 255), so their raw order is total.
        private void AddLeaf(in PbtStorageTreeKey key, ValueHash256 value) => leaves.Add(key.Bytes, value.Bytes);
    }
}
