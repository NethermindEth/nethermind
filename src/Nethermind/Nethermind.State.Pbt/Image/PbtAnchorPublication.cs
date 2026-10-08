// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat;
using Nethermind.State.Pbt.Persistence;

namespace Nethermind.State.Pbt.Image;

/// <summary>Imports EIP-8347 artifacts as the native PBT state at their anchor block.</summary>
/// <remarks>
/// The provenance marker written before staging makes the import idempotent: a restart with the same source takes
/// the fast path without re-verifying the whole image, a restart with another source is refused, and an interrupted
/// staging is wiped and redone.
/// </remarks>
internal sealed class PbtAnchorPublication(
    PbtRocksDbPersistence target,
    IColumnsDb<PbtColumns> targetDb,
    PbtPersistenceCoordinator coordinator,
    IPbtConfig config,
    ILogManager logManager)
{
    private static readonly byte[] _provenanceKey = "migrationPreparedAnchor"u8.ToArray();

    /// <summary>Which anchor a native PBT database was seeded from, so a restart against a different one is refused.</summary>
    private sealed record AnchorProvenance(string ChainId, string GenesisHash, string AnchorHash, long AnchorNumber, string AnchorMptRoot);

    private static byte[] Provenance(PbtImageAnchor anchor) => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
        new AnchorProvenance(anchor.ChainId, anchor.GenesisHash.ToString(), anchor.Header.Hash!.ToString(),
            (long)anchor.Header.Number, anchor.Header.StateRoot!.ToString()));
    // Below DbOnTheRocks.RocksDbWriteBatch.MaxWritesOnNoWal, so a no-WAL batch is written by its flusher, not inline by the reader.
    internal const int BatchSize = 255;
    private const string StagePhase = "PBT import stage";
    private const string FoldPhase = "PBT import fold";
    private readonly ILogger _logger = logManager.GetClassLogger<PbtAnchorPublication>();

    /// <summary>Imports the native PBT state at the anchor from a snapshot, optionally verified by preimages.</summary>
    /// <remarks>The preimages are not ingested: they only rebuild the anchor's MPT root over the staged state. Without
    /// them nothing ties the snapshot to the anchor's MPT root; only its own claimed PBT root is checked.</remarks>
    public async Task<ValueHash256> PublishSnapshot(Stream snapshot, Stream? preimages,
        PbtImageAnchor anchor, string scratchDirectory, Func<bool> isAnchorCurrent, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        anchor.Validate();
        Stopwatch importing = Stopwatch.StartNew();
        if (ImportedEarlier(anchor) is { } imported) return imported;

        ValueHash256 claimedRoot = PbtSnapshotCodec.ReadRoot(snapshot);
        if (!isAnchorCurrent()) throw new InvalidOperationException("Migration anchor changed before the import.");
        // The snapshot's sections do not share one key order, so its read offset is what measures a pass.
        float snapshotLength = snapshot.Length;
        return await ImportLeaves(anchor, scratchDirectory, token => SnapshotLeaves(snapshot, token), _ => snapshot.Position / snapshotLength,
            claimedRoot, preimages, isAnchorCurrent, importing, cancellationToken);
    }

    /// <summary>Stages the logical state of an ascending leaf stream, then folds the same stream into the tree.</summary>
    /// <remarks>Preimages are verified before the fold, which is what marks the target valid, so a refused import
    /// stays unpublished and a restart wipes its staging.</remarks>
    private async Task<ValueHash256> ImportLeaves(PbtImageAnchor anchor, string scratchDirectory,
        Func<CancellationToken, IEnumerable<RebuildEntry>> leaves, Func<ulong, float> fraction, ValueHash256 claimedRoot, Stream? preimages,
        Func<bool> isAnchorCurrent, Stopwatch importing, CancellationToken cancellationToken)
    {
        PrepareStaging(anchor, cancellationToken);
        (ulong Accounts, ulong Slots, long CodeChunks) staged;
        using (LogicalBatch batch = new(target, config.ImportConcurrency > 0 ? config.ImportConcurrency : Environment.ProcessorCount, cancellationToken))
        {
            staged = PbtLeafStaging.Stage(batch, Reported(StagePhase, leaves(cancellationToken), fraction), cancellationToken);
            batch.Commit();
        }
        PbtLeafStaging.RebuildCodes(target, staged.CodeChunks, logManager, cancellationToken);
        if (preimages is not null)
        {
            using (IPbtPersistence.IReader reader = target.CreateReader())
                if (PbtImageVerifier.Verify(preimages, reader, anchor, scratchDirectory, config.MigrationVerifyBucketBytes, config.ExportSortBufferBytes,
                        config.ImportConcurrency, logManager, cancellationToken) != (staged.Accounts, staged.Slots))
                    throw new InvalidDataException("Snapshot holds state its preimages do not list.");
            if (!isAnchorCurrent()) throw new InvalidOperationException("Migration anchor or MPT state changed during verification.");
        }
        ValueHash256 root = await Fold(token => Reported(FoldPhase, leaves(token), fraction), new StateId(anchor.Header), claimedRoot, cancellationToken);
        Finish(anchor, root, staged.Accounts, staged.Slots, importing, cancellationToken);
        return root;
    }

    /// <summary>The native root when this anchor was already imported, or null when the import has yet to run.</summary>
    private ValueHash256? ImportedEarlier(PbtImageAnchor anchor)
    {
        if (!target.IsValid || targetDb.GetColumnDb(PbtColumns.Metadata).Get(_provenanceKey) is not { } prepared) return null;
        if (!prepared.AsSpan().SequenceEqual(Provenance(anchor)))
            throw new InvalidDataException("The native PBT database was imported from another migration source.");
        if (_logger.IsInfo) _logger.Info($"PBT migration anchor {anchor.Header.ToString(BlockHeader.Format.Short)} was imported earlier; reusing it.");
        using IPbtPersistence.IReader reader = target.CreateReader();
        return reader.CurrentRoot;
    }

    private void PrepareStaging(PbtImageAnchor anchor, CancellationToken cancellationToken)
    {
        RecoverStaging(anchor, cancellationToken);
        if (target.IsValid) throw new InvalidOperationException("An unpublished populated PBT target requires recovery, not replacement.");
        using IPbtPersistence.IReader reader = target.CreateReader();
        if (reader.CurrentState != StateId.PreGenesis)
            throw new InvalidOperationException("PBT anchor staging must start empty.");
    }

    /// <summary>Folds the ascending leaves into the tree and publishes them as <paramref name="anchorState"/>, unless the root differs from <paramref name="expectedRoot"/>.</summary>
    private async Task<ValueHash256> Fold(Func<CancellationToken, IEnumerable<RebuildEntry>> leaves, StateId anchorState, ValueHash256? expectedRoot, CancellationToken cancellationToken)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Channel<ArrayPoolList<RebuildEntry>> channel = Channel.CreateBounded<ArrayPoolList<RebuildEntry>>(2);
        Task producer = Task.Run(async () =>
        {
            try
            {
                using (PbtRebuilder.EntrySink sink = new(channel.Writer, 2048, linked.Token))
                {
                    foreach (RebuildEntry entry in leaves(linked.Token)) await sink.Add(entry);
                    await sink.Complete();
                }
                channel.Writer.TryComplete();
            }
            catch (Exception exception) { channel.Writer.TryComplete(exception); throw; }
        }, CancellationToken.None);
        try
        {
            ValueHash256 root = await new PbtRebuilder(target, config, config.ImportConcurrency, logManager).Rebuild(channel.Reader, anchorState, linked.Token, 16_384, expectedRoot);
            await producer;
            return root;
        }
        finally
        {
            await linked.CancelAsync();
            try { await producer; }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
            finally { while (channel.Reader.TryRead(out ArrayPoolList<RebuildEntry>? chunk)) chunk.Dispose(); }
        }
    }

    private void Finish(PbtImageAnchor anchor, in ValueHash256 root, ulong stagedAccounts, ulong stagedSlots, Stopwatch importing, CancellationToken cancellationToken)
    {
        foreach (PbtColumns column in targetDb.ColumnKeys) targetDb.GetColumnDb(column).SyncWal();
        cancellationToken.ThrowIfCancellationRequested();

        // The import bypassed the live persistence: reload what it cached before the write.
        coordinator.ResetPersistedStateId();
        if (_logger.IsInfo)
            _logger.Info($"Imported the PBT migration anchor {anchor.Header.ToString(BlockHeader.Format.Short)} with root {root}: " +
                $"{stagedAccounts:N0} accounts and {stagedSlots:N0} slots in {importing.Elapsed:hh\\:mm\\:ss}.");
    }

    /// <param name="fraction">The fraction of the pass done after the given number of leaves, sampled on the reading
    /// thread since a snapshot's offset briefly moves while its reader re-reads the header section.</param>
    private IEnumerable<RebuildEntry> Reported(string phase, IEnumerable<RebuildEntry> leaves, Func<ulong, float> fraction)
    {
        ulong read = 0;
        float walked = 0;
        using ProgressReporter progress = PbtImageProgress.Start(phase, "leaf", 0, logManager);
        progress.Logger.SetFormat(p => PbtImageProgress.Format(phase, walked, PbtImageProgress.Counted("leaf", p)));
        foreach (RebuildEntry entry in leaves)
        {
            walked = fraction(++read);
            progress.Update(read);
            yield return entry;
        }
    }

    private static IEnumerable<RebuildEntry> SnapshotLeaves(Stream snapshot, CancellationToken cancellationToken)
    {
        snapshot.Position = 0;
        foreach (RebuildEntry entry in PbtSnapshotCodec.ReadLeaves(snapshot, cancellationToken)) yield return entry;
    }

    private void RecoverStaging(PbtImageAnchor anchor, CancellationToken token)
    {
        byte[] provenance = Provenance(anchor);
        IDb metadata = targetDb.GetColumnDb(PbtColumns.Metadata);
        byte[]? prepared = metadata.Get(_provenanceKey);
        if (prepared is not null)
        {
            if (!prepared.AsSpan().SequenceEqual(provenance))
                throw new InvalidDataException("Unpublished native anchor belongs to another bootstrap source.");
            foreach (PbtColumns column in targetDb.ColumnKeys)
            {
                token.ThrowIfCancellationRequested();
                IDb records = targetDb.GetColumnDb(column);
                if (column != PbtColumns.Metadata)
                    ((IRangeRemovableKeyValueStore)records).RemoveRange([], PbtColumnSweep.PastEveryKey());
                else
                    foreach (byte[] recordKey in records.GetAllKeys())
                        if (!recordKey.AsSpan().SequenceEqual(_provenanceKey) && !PbtRocksDbPersistence.IsSchemaStamp(recordKey))
                            records.Remove(recordKey);
                records.SyncWal();
            }
        }
        else
        {
            foreach (PbtColumns column in targetDb.ColumnKeys)
                foreach (byte[] recordKey in targetDb.GetColumnDb(column).GetAllKeys())
                    if (column != PbtColumns.Metadata || !PbtRocksDbPersistence.IsSchemaStamp(recordKey))
                        throw new InvalidDataException("Populated native anchor has no matching prepared provenance.");
            metadata.Set(_provenanceKey, provenance);
            metadata.SyncWal();
        }
    }

    /// <summary>Compiles staged writes into no-WAL batches on the caller's thread and writes them on parallel flushers.</summary>
    /// <remarks>Staged keys are unique, so the order the flushers write in does not matter. Nothing is durable until
    /// <see cref="Commit"/> flushes the write buffers; an interrupted staging is wiped by <see cref="RecoverStaging"/>.</remarks>
    internal sealed class LogicalBatch : IDisposable
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
            return _batch ??= _target.CreateStagingWriteBatch(WriteFlags.DisableWAL);
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
