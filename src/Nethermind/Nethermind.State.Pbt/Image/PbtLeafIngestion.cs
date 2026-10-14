// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Threading;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Pbt;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Collections;
using Nethermind.State.Pbt.Common;
using Nethermind.State.Pbt.Persistence;

namespace Nethermind.State.Pbt.Image;

/// <summary>Ingests an ascending PBT leaf stream into an empty target: stages its logical state while folding its tree.</summary>
public static class PbtLeafIngestion
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
    /// The stream is read once, feeding both. Up to a fold window of leaves is buffered ahead of the fold, so staging keeps
    /// going while a window commits. The staging and the fold write disjoint columns and neither reads the other's writes,
    /// so the only ordering is the publication, which waits for the staging and <paramref name="verifyStaged"/>, and is
    /// refused when the root differs from <paramref name="expectedRoot"/>.
    /// </remarks>
    /// <param name="fraction">The fraction of the pass done after the given number of leaves.</param>
    /// <param name="concurrency">Maximum staging write flushers and code reconstruction workers; zero uses the processor count.</param>
    /// <param name="windowSize">Maximum leaves per fold window; zero uses the rebuilder's default.</param>
    /// <param name="verifyStaged">Checks the staged accounts and slots before the tree is published.</param>
    /// <param name="scratchDirectory">Caller-owned directory for the code reassembly's spools.</param>
    /// <param name="sortBufferBytes">Bytes buffered per code reassembly spool before a sorted run spills.</param>
    /// <returns>The tree root and the staged accounts and slots.</returns>
    public static async Task<(ValueHash256 Root, ulong Accounts, ulong Slots)> Ingest(PbtRocksDbPersistence target, PbtRebuilder rebuilder,
        Func<CancellationToken, IEnumerable<RebuildEntry>> leaves, Func<ulong, float> fraction, int concurrency, StateId targetState, int windowSize,
        ValueHash256? expectedRoot, Action<CancellationToken> verifyStaged, string scratchDirectory, int sortBufferBytes, ILogManager logManager,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Channel<ArrayPoolList<RebuildEntry>> channel = Channel.CreateBounded<ArrayPoolList<RebuildEntry>>(
            (windowSize > 0 ? windowSize : PbtSortedLeafFold.DefaultWindowSize) / PbtSortedLeafFold.FoldChunkSize + 1);
        Task<(ulong Accounts, ulong Slots)> staging = Task.Run(() =>
        {
            try
            {
                (ulong Accounts, ulong Slots) staged = Stage(target, PbtSortedLeafFold.Teed(Reported(leaves(linked.Token), fraction, logManager), channel.Writer, linked.Token),
                    concurrency, scratchDirectory, sortBufferBytes, logManager, linked.Token);
                verifyStaged(linked.Token);
                return staged;
            }
            catch (Exception exception) { channel.Writer.TryComplete(exception); throw; }
        }, CancellationToken.None);
        try
        {
            ValueHash256 root = await rebuilder.Rebuild(channel.Reader, targetState, linked.Token, windowSize, expectedRoot, staging);
            (ulong accounts, ulong slots) = await staging;
            return (root, accounts, slots);
        }
        finally
        {
            await linked.CancelAsync();
            try { await staging; }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
            finally { while (channel.Reader.TryRead(out ArrayPoolList<RebuildEntry>? chunk)) chunk.Dispose(); }
        }
    }

    private static (ulong Accounts, ulong Slots) Stage(PbtRocksDbPersistence target, IEnumerable<RebuildEntry> leaves, int concurrency,
        string scratchDirectory, int sortBufferBytes, ILogManager logManager, CancellationToken cancellationToken)
    {
        int workers = concurrency > 0 ? concurrency : Environment.ProcessorCount;
        (ulong Accounts, ulong Slots) staged;
        using PbtCodeReassembly codes = new(target, scratchDirectory, sortBufferBytes, workers, logManager, cancellationToken);
        using (LogicalBatch batch = new(target, workers, cancellationToken))
        {
            staged = Stage(batch, leaves, codes, cancellationToken);
            batch.Commit();
        }
        codes.Complete();
        return staged;
    }

    /// <summary>Stages the logical accounts, code and slot runs of an ascending PBT leaf stream, refusing noncanonical leaves.</summary>
    /// <remarks>
    /// Leaves carry no storage roots and PBT never treats one as authoritative, so accounts are staged as their stem leaves.
    /// Code chunks are keyed by a hash of their code hash, so no bytecode can be reassembled as its chunks stream past: they
    /// are handed to a <see cref="PbtCodeReassembly"/>, which reassembles each bytecode once the code
    /// section ends.
    /// </remarks>
    /// <returns>The staged accounts and slots.</returns>
    public static (ulong Accounts, ulong Slots) Stage(LogicalBatch batch, IEnumerable<RebuildEntry> leaves,
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

        foreach (RebuildEntry entry in leaves)
        {
            cancellationToken.ThrowIfCancellationRequested();
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

    /// <param name="fraction">The fraction of the pass done after the given number of leaves, sampled on the reading
    /// thread since a snapshot's offset briefly moves while its reader re-reads the header section.</param>
    private static IEnumerable<RebuildEntry> Reported(IEnumerable<RebuildEntry> leaves, Func<ulong, float> fraction, ILogManager logManager)
    {
        ulong read = 0;
        float walked = 0;
        using ProgressReporter progress = PbtImageProgress.Start(Phase, "leaf", 0, logManager);
        progress.Logger.SetFormat(p => PbtImageProgress.Format(Phase, walked, PbtImageProgress.Counted("leaf", p)));
        foreach (RebuildEntry entry in leaves)
        {
            walked = fraction(++read);
            progress.Update(read);
            yield return entry;
        }
    }

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
