// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Threading;
using Nethermind.Db;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Pbt;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Collections;
using Nethermind.State.Pbt.Common;
using Nethermind.State.Pbt.Migration;
using Nethermind.State.Pbt.Persistence;

namespace Nethermind.State.Pbt.Image;

/// <summary>Ingests an ascending PBT leaf stream into an empty target: stages its logical state while folding its tree.</summary>
public sealed class PbtLeafIngestion(PbtRocksDbPersistence target, PbtRebuilder rebuilder, IDbFactory dbFactory, IPbtConfig config, ILogManager logManager)
{
    // Below DbOnTheRocks.RocksDbWriteBatch.MaxWritesOnNoWal, so a no-WAL batch is written by its flusher, not inline by the reader.
    public const int BatchSize = 255;
    private const string Phase = "PBT import";
    /// <summary>Local byte budget for whole code plus its 32-byte chunk encoding; not a deployment-code limit.
    /// Exhaustion is retryable resource unavailability, not invalid state.</summary>
    private const ulong MaxBufferedCodeBytes = 256 * 1024 * 1024;

    /// <summary>Stages the logical accounts, slots and code of the leaves while folding them into the tree, and publishes the tree
    /// as <paramref name="targetState"/> once the staging is verified.</summary>
    /// <remarks>
    /// The fold reads the stream once and tees each leaf to the staging, which may fall up to a fold window behind, so
    /// staging keeps going while a window folds and commits. The staging and the fold write disjoint columns and neither
    /// reads the other's writes, so the only ordering is the publication, which waits for the staging and <paramref name="verifyStaged"/>, and is
    /// refused when the root differs from <paramref name="expectedRoot"/>.
    /// </remarks>
    /// <param name="verifyStaged">Checks the staged accounts and slots before the tree is published.</param>
    /// <returns>The tree root and the staged accounts and slots.</returns>
    public async Task<(ValueHash256 Root, ulong Accounts, ulong Slots)> Ingest(Func<CancellationToken, IEnumerable<RebuildEntry>> leaves,
        StateId targetState, ValueHash256? expectedRoot, Action<CancellationToken> verifyStaged, CancellationToken cancellationToken)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        IEnumerable<RebuildEntry> sourceLeaves = leaves(linked.Token);
        IEnumerable<RebuildEntry> reportedLeaves = Reported(sourceLeaves, logManager);
        IEnumerator<RebuildEntry> reportedEnumerator = reportedLeaves.GetEnumerator();
        int windowSize = config.ImportWindowSize > 0 ? config.ImportWindowSize : PbtSortedLeafFold.DefaultWindowSize;
        TeeEnumerator<RebuildEntry> foldLeaves = new(reportedEnumerator, windowSize);
        IEnumerator<RebuildEntry> stagingLeaves = foldLeaves.Tee();
        Task<(ulong Accounts, ulong Slots)> staging = Task.Run(() =>
        {
            try
            {
                (ulong Accounts, ulong Slots) staged;
                using (stagingLeaves) staged = Stage(stagingLeaves, linked.Token);
                verifyStaged(linked.Token);
                return staged;
            }
            catch
            {
                // Otherwise the fold would go on reading and folding the rest of the source for nothing.
                linked.Cancel();
                throw;
            }
        }, CancellationToken.None);
        try
        {
            ValueHash256 root;
            using (foldLeaves) root = await Task.Run(() => rebuilder.Rebuild(foldLeaves, config.ImportWindowSize, linked.Token), CancellationToken.None);
            linked.Token.ThrowIfCancellationRequested();
            target.Flush();
            if (expectedRoot is { } expected && root != expected)
                throw new InvalidDataException("Staged PBT root differs from the snapshot's claimed root.");
            (ulong accounts, ulong slots) = await staging;
            linked.Token.ThrowIfCancellationRequested();
            using IPbtPersistence.IWriteBatch batch = target.CreateWriteBatch(StateId.PreGenesis, targetState, root);
            batch.Commit();
            return (root, accounts, slots);
        }
        finally
        {
            await linked.CancelAsync();
            try { await staging; }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        }
    }

    private (ulong Accounts, ulong Slots) Stage(IEnumerator<RebuildEntry> leaves, CancellationToken cancellationToken)
    {
        int workers = config.ImportConcurrency > 0 ? config.ImportConcurrency : Environment.ProcessorCount;
        string scratchDirectory = Path.Combine(PbtMigrationAnchor.ScratchDirectory(dbFactory, "import"), $"pbt-import-codes-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratchDirectory);
        try
        {
            (ulong Accounts, ulong Slots) staged;
            using PbtCodeReassembly codes = new(target, scratchDirectory, config.ImportSortBufferBytes, workers, logManager, cancellationToken);
            using (LogicalBatch batch = new(target, workers, cancellationToken))
            {
                staged = Stage(batch, leaves, codes, cancellationToken);
                batch.Commit();
            }
            codes.Complete();
            return staged;
        }
        finally
        {
            Directory.Delete(scratchDirectory, recursive: true);
        }
    }

    /// <summary>Stages the logical accounts, code and slot runs of an ascending PBT leaf stream, refusing noncanonical leaves.</summary>
    /// <remarks>
    /// Leaves carry no storage roots and PBT never treats one as authoritative, so accounts are staged as their stem leaves.
    /// Code chunks are keyed by a hash of their code hash, so no bytecode can be reassembled as its chunks stream past: they
    /// are handed to a <see cref="PbtCodeReassembly"/>, which reassembles each bytecode once the code
    /// section ends.
    /// </remarks>
    /// <returns>The staged accounts and slots.</returns>
    public static (ulong Accounts, ulong Slots) Stage(LogicalBatch batch, IEnumerator<RebuildEntry> leaves,
        PbtCodeReassembly codes, CancellationToken cancellationToken)
    {
        ulong accounts = 0;
        ulong slots = 0;
        ValueHash256? stem = null;
        ValueHash256 basicData = default;
        ValueHash256? codeHash = null;
        ValueHash256? delegation = null;
        PbtVariableTreeKey? runKey = null;
        PackedSlotRun run = SlotRun.Empty;

        while (leaves.MoveNext())
        {
            cancellationToken.ThrowIfCancellationRequested();
            RebuildEntry entry = leaves.Current;
            ReadOnlySpan<byte> key = entry.Key.Bytes;
            if (key[0] == Eip8297KeyDerivation.CodeZone)
            {
                FlushAccount();
                codes.AddChunk((PbtPath)entry.Key, entry.Leaf);
                continue;
            }
            if (key[0] == Eip8297KeyDerivation.AccountZone)
            {
                ValueHash256 addressHash = Eip8297KeyDerivation.AddressHashOf(entry.Key);
                if (stem != addressHash)
                {
                    FlushAccount();
                    stem = addressHash;
                }
                switch (key[^1])
                {
                    case PbtKeyDerivation.BasicDataLeafKey:
                        basicData = entry.Leaf;
                        continue;
                    case PbtKeyDerivation.CodeHashLeafKey:
                        codeHash = entry.Leaf;
                        continue;
                    case PbtKeyDerivation.DelegationLeafKey:
                        delegation = entry.Leaf;
                        continue;
                    case < PbtKeyDerivation.HeaderStorageOffset or >= PbtKeyDerivation.HeaderStorageOffset + PbtKeyDerivation.HeaderStorageSlots:
                        throw new InvalidDataException("Snapshot holds a leaf at a reserved account sub-index.");
                }
            }
            else
            {
                FlushAccount();
                codes.EndChunks();
            }

            slots++;
            PbtVariableTreeKey slotRunKey = SlotRun.RunKey(entry.Key);
            if (runKey != slotRunKey) FlushRun();
            runKey = slotRunKey;
            PackedSlotRun previous = run;
            run = previous.With(SlotRun.IndexOf(entry.Key), entry.Leaf.ToUInt256());
            SlotRun.Return(previous);
        }
        FlushAccount();
        codes.EndChunks();
        FlushRun();
        return (accounts, slots);

        void FlushAccount()
        {
            if (stem is not { } addressHash) return;
            if (!PbtKeyDerivation.IsCanonicalBasicData(basicData.Bytes))
                throw new InvalidDataException("Nonzero basic-data version or reserved bytes.");
            int codeSize = (int)PbtKeyDerivation.ReadBasicDataCodeSize(basicData.Bytes);
            PbtKeyDerivation.UnpackBasicData(basicData.Bytes, out ulong nonce, out UInt256 balance);
            if (nonce == 0 && balance.IsZero && codeSize == 0)
                throw new InvalidDataException("Empty account violates EIP-7523.");

            PbtAccount account;
            if (delegation is { } delegated)
            {
                if (codeSize != PbtAccount.DelegationLength || codeHash is not null || delegated.Bytes[PbtAccount.DelegationLength..].IndexOfAnyExcept((byte)0) >= 0 ||
                    !Eip7702Constants.IsDelegatedCode(delegated.Bytes[..PbtAccount.DelegationLength]))
                    throw new InvalidDataException("Invalid delegation header.");
                account = new PbtAccount(basicData, delegated, IsDelegation: true);
                batch.Next().SetCode(account.CodeHash, new CodeInfo(delegated.Bytes[..PbtAccount.DelegationLength].ToArray()));
            }
            else
            {
                account = new PbtAccount(basicData, codeHash ?? throw new InvalidDataException("Account has no code-hash leaf."), IsDelegation: false);
                if (codeSize == 0)
                {
                    if (account.HasCode) throw new InvalidDataException("Codeless account has a nonempty code hash.");
                }
                else
                {
                    if ((ulong)codeSize + (ulong)PbtKeyDerivation.CodeChunkCount(codeSize) * PbtKeyDerivation.CodeChunkSize > MaxBufferedCodeBytes)
                        throw new PbtImageResourceLimitException("Code staging requires a larger local buffering budget.");
                    codes.AddCode(account.CodeHash, (uint)codeSize);
                }
            }
            batch.Next().SetAccount(addressHash, account);
            accounts++;
            stem = null;
            basicData = default;
            codeHash = null;
            delegation = null;
        }

        void FlushRun()
        {
            if (runKey is not { } key) return;
            if (key.Length == PbtPath.KeyLength) batch.Next().SetSlotRun((PbtPath)key, run);
            else batch.Next().SetSlotRun((PbtStoragePath)key, run);
            SlotRun.Return(run);
            run = SlotRun.Empty;
            runKey = null;
        }
    }

    /// <summary>The ascending leaves of a spool of tree keys and leaf values.</summary>
    public static IEnumerable<RebuildEntry> SpoolLeaves(SortedSpool spool)
    {
        using SortedSpool.Cursor cursor = spool.Read();
        while (cursor.MoveNext()) yield return new RebuildEntry(new PbtVariableTreeKey(cursor.Key), new ValueHash256(cursor.Value));
    }

    /// <remarks>Progress is the keyspace position of the leaf's path after its zone byte, so it restarts in each zone.</remarks>
    private static IEnumerable<RebuildEntry> Reported(IEnumerable<RebuildEntry> leaves, ILogManager logManager)
    {
        ulong read = 0;
        float walked = 0;
        string phase = Phase;
        using ProgressReporter progress = PbtImageProgress.Start(Phase, "leaf", 0, logManager);
        progress.Logger.SetFormat(p => PbtImageProgress.Format(phase, walked, PbtImageProgress.Counted("leaf", p)));
        foreach (RebuildEntry entry in leaves)
        {
            phase = ZonePhase(entry.Key.Bytes[0]);
            walked = PbtImageProgress.KeyspaceFraction(new ValueHash256(entry.Key.Bytes.Slice(1, ValueHash256.MemorySize)));
            progress.Update(++read);
            yield return entry;
        }
    }

    private static string ZonePhase(byte zone) => zone switch
    {
        Eip8297KeyDerivation.AccountZone => "PBT import (Account)",
        Eip8297KeyDerivation.CodeZone => "PBT import (Code)",
        Eip8297KeyDerivation.StorageZone => "PBT import (Storage)",
        _ => Phase
    };

    /// <summary>Compiles staged writes into no-WAL batches on the caller's thread and writes them on parallel flushers.</summary>
    /// <remarks>Staged keys are unique, so the order the flushers write in does not matter. Nothing is durable until
    /// <see cref="Commit"/> flushes the write buffers; the caller wipes an interrupted staging before retrying.</remarks>
    public sealed class LogicalBatch : IDisposable
    {
        private readonly PbtRocksDbPersistence _target;
        private readonly CancellationTokenSource _cancellation;
        private readonly Task[] _flushers;
        private readonly Channel<IPbtPersistence.IWriteBatch> _pending;
        private ExceptionDispatchInfo? _failure;
        private IPbtPersistence.IWriteBatch? _batch;
        private int _count;

        public LogicalBatch(PbtRocksDbPersistence target, int flusherCount, CancellationToken cancellationToken)
        {
            _target = target;
            _flushers = new Task[flusherCount];
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _pending = Channel.CreateBounded<IPbtPersistence.IWriteBatch>(
                new BoundedChannelOptions(2 * flusherCount) { SingleWriter = true });
            for (int i = 0; i < _flushers.Length; i++)
                _flushers[i] = Task.Run(Flush, CancellationToken.None);
        }

        public IPbtPersistence.IWriteBatch Next()
        {
            if (_count == BatchSize) Enqueue();
            _count++;
            return _batch ??= _target.CreateStagingWriteBatch();
        }

        /// <summary>Waits for every staged write, then flushes the write buffers so the no-WAL writes are durable.</summary>
        public void Commit()
        {
            if (_batch is not null) Enqueue();
            _pending.Writer.Complete();
            WaitFlushers();
            _target.Flush();
        }

        public void Dispose()
        {
            _pending.Writer.TryComplete();
            _cancellation.Cancel();
            Task.WaitAll(_flushers);
            _batch?.Dispose();
            while (_pending.Reader.TryRead(out IPbtPersistence.IWriteBatch? batch)) batch.Dispose();
            _cancellation.Dispose();
        }

        private void Enqueue()
        {
            try
            {
                if (!_pending.Writer.TryWrite(_batch!)) _pending.Writer.WriteAsync(_batch!, _cancellation.Token).AsTask().GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                WaitFlushers();
                throw;
            }
            _batch = null;
            _count = 0;
        }

        private async Task Flush()
        {
            try
            {
                await foreach (IPbtPersistence.IWriteBatch batch in _pending.Reader.ReadAllAsync(_cancellation.Token))
                    using (batch) batch.Commit();
            }
            catch (Exception exception)
            {
                Interlocked.CompareExchange(ref _failure, ExceptionDispatchInfo.Capture(exception), null);
                _cancellation.Cancel();
            }
        }

        /// <summary>Waits for the flushers to stop and rethrows the first failure among them.</summary>
        private void WaitFlushers()
        {
            Task.WaitAll(_flushers);
            _failure?.Throw();
        }
    }
}
