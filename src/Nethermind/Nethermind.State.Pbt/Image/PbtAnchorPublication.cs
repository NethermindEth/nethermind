// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Threading.Channels;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Logging;
using Nethermind.Pbt;
using Nethermind.State.Pbt.Persistence;

namespace Nethermind.State.Pbt.Image;

/// <summary>Verifies a portable EIP-8347 image and imports it as the native PBT state at its anchor block.</summary>
/// <remarks>
/// The provenance marker written before staging makes the import idempotent: a restart with the same source takes
/// the fast path without re-verifying the whole image, a restart with another source is refused, and an interrupted
/// staging is wiped and redone. When PBT already holds the anchor (a genesis bootstrap mirrors genesis before this
/// runs) the image is only verified against it.
/// </remarks>
internal sealed class PbtAnchorPublication(
    PbtRocksDbPersistence target,
    IColumnsDb<PbtColumns> targetDb,
    IPbtPersistence persistence,
    IPbtDbManager manager,
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
    private const int BatchSize = 4096;
    private const string StagePhase = "PBT anchor staging";

    /// <summary>How many of one account's slot runs staging buffers before spilling them to the target.</summary>
    internal int MaxBufferedRuns { get; init; } = 4096;
    private readonly ILogger _logger = logManager.GetClassLogger<PbtAnchorPublication>();

    public async Task<ValueHash256> Publish(Stream snapshot, Stream preimages,
        PbtImageAnchor anchor, string scratchDirectory, Func<bool> isAnchorCurrent, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Stopwatch importing = Stopwatch.StartNew();
        byte[] provenance = Provenance(anchor);
        IDb metadata = targetDb.GetColumnDb(PbtColumns.Metadata);
        StateId anchorState = new(anchor.Header);
        if (target.IsValid && metadata.Get(_provenanceKey) is { } prepared)
        {
            if (!prepared.AsSpan().SequenceEqual(provenance))
                throw new InvalidDataException("The native PBT database was imported from another migration source.");
            if (_logger.IsInfo) _logger.Info($"PBT migration anchor {anchor.Header.ToString(BlockHeader.Format.Short)} was imported earlier; reusing it.");
            using IPbtPersistence.IReader reader = persistence.CreateReader();
            return reader.CurrentRoot;
        }

        // Verification and folding must read the same immutable bytes, even if an input file is replaced.
        string copyPath = Path.Combine(scratchDirectory, $"pbt-source-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratchDirectory);
        try
        {
            await using FileStream copiedSnapshot = new(copyPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            await snapshot.CopyToAsync(copiedSnapshot, cancellationToken);
            copiedSnapshot.Position = 0;
            using PbtVerifiedImage image = PbtImageVerifier.Verify(copiedSnapshot, preimages, anchor, scratchDirectory, logManager, cancellationToken);
            if (!isAnchorCurrent()) throw new InvalidOperationException("Migration anchor or MPT state changed during verification.");

            if (manager.HasStateForBlock(anchorState))
            {
                ValueHash256 held;
                using (PbtReadOnlySnapshotBundle bundle = manager.GatherReadOnlyBundle(anchorState)) held = bundle.TreeRoot;
                if (held != image.PbtRoot) throw new InvalidDataException("The PBT state already held at the anchor differs from the verified image.");
                metadata.Set(_provenanceKey, provenance);
                metadata.SyncWal();
                return held;
            }

            RecoverStaging(targetDb, anchor, cancellationToken);
            if (target.IsValid) throw new InvalidOperationException("An unpublished populated PBT target requires recovery, not replacement.");
            using (IPbtPersistence.IReader reader = target.CreateReader())
                if (reader.CurrentState != StateId.PreGenesis)
                    throw new InvalidOperationException("PBT anchor staging must start empty.");

            ulong stagedAccounts = 0;
            ulong stagedSlots = 0;
            using (LogicalBatch batch = new(target))
            {
                // The image lists an account's slots in hash order, so a run completes only once the account ends. A
                // bounded buffer spills a large account's runs early and merges its later slots into the staged rows.
                Dictionary<PbtStorageTreeKey, ISlotRun> runs = [];
                Address? slotsAddress = null;
                bool spilled = false;
                using ProgressReporter progress = PbtImageProgress.Start(StagePhase, "acc", 0, logManager);
                progress.Logger.SetFormat(p => $"{PbtImageProgress.Format(StagePhase, "acc", p)} | {stagedSlots,15:N0} slot");
                image.Replay((address, account, code) =>
                {
                    progress.Update(++stagedAccounts);
                    batch.Next().SetAccount(PbtKeyDerivation.AddressKeyHash(address), account);
                    if (code.Length != 0) batch.Next().SetCode(account.CodeHash.ValueHash256, new CodeInfo(code));
                }, (address, slot, value) =>
                {
                    stagedSlots++;
                    if (address != slotsAddress)
                    {
                        FlushRuns();
                        spilled = false;
                    }
                    else if (runs.Count == MaxBufferedRuns)
                    {
                        FlushRuns();
                        spilled = true;
                    }
                    slotsAddress = address;
                    PbtStorageTreeKey key = PbtStateKey.Storage(address, slot);
                    PbtStorageTreeKey runKey = SlotRun.RunKey(key);
                    ISlotRun previous = runs.TryGetValue(runKey, out ISlotRun? held) ? held : SlotRun.Empty;
                    runs[runKey] = previous.With(SlotRun.IndexOf(key), EvmWordSlot.FromStripped(value.Bytes));
                    SlotRun.Return(previous);
                }, cancellationToken);
                FlushRuns();
                batch.Commit();

                void FlushRuns()
                {
                    if (runs.Count == 0) return;
                    if (spilled) batch.Flush();
                    using IPbtPersistence.IReader? staged = spilled ? target.CreateReader() : null;
                    foreach ((PbtStorageTreeKey runKey, ISlotRun run) in runs)
                    {
                        ISlotRun whole = staged is null ? run : MergeInto(staged.GetSlotRun(runKey), run);
                        batch.Next().SetSlotRun(runKey, whole);
                        SlotRun.Return(whole);
                    }
                    runs.Clear();
                }
            }

            copiedSnapshot.Position = 0;
            (_, ulong count) = PbtSnapshotCodec.ReadHeader(copiedSnapshot);
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Channel<ArrayPoolList<RebuildEntry>> channel = Channel.CreateBounded<ArrayPoolList<RebuildEntry>>(2);
            Task producer = Task.Run(async () =>
            {
                try
                {
                    ArrayPoolList<RebuildEntry>? chunk = new(2048);
                    try
                    {
                        foreach (RebuildEntry entry in PbtSnapshotCodec.ReadLeaves(copiedSnapshot, count, linked.Token))
                        {
                            chunk.Add(entry);
                            if (chunk.Count != 2048) continue;
                            await channel.Writer.WriteAsync(chunk, linked.Token);
                            chunk = null;
                            chunk = new(2048);
                        }
                        if (chunk.Count != 0)
                        {
                            await channel.Writer.WriteAsync(chunk, linked.Token);
                            chunk = null;
                        }
                    }
                    finally { chunk?.Dispose(); }
                    channel.Writer.TryComplete();
                }
                catch (Exception exception) { channel.Writer.TryComplete(exception); throw; }
            }, CancellationToken.None);
            ValueHash256 root;
            try
            {
                root = await new PbtRebuilder(target, config, logManager).Rebuild(channel.Reader, anchorState, linked.Token, 16_384, WriteFlags.None);
                await producer;
            }
            finally
            {
                await linked.CancelAsync();
                try { await producer; }
                catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
                finally { while (channel.Reader.TryRead(out ArrayPoolList<RebuildEntry>? chunk)) chunk.Dispose(); }
            }
            if (root != image.PbtRoot) throw new InvalidDataException("Staged PBT root differs from the verified image.");
            foreach (PbtColumns column in targetDb.ColumnKeys) targetDb.GetColumnDb(column).SyncWal();
            cancellationToken.ThrowIfCancellationRequested();

            // The import bypassed the live persistence: drop what it cached before the write.
            coordinator.ResetPersistedStateId();
            persistence.ClearCaches();
            if (_logger.IsInfo)
                _logger.Info($"Imported the PBT migration anchor {anchor.Header.ToString(BlockHeader.Format.Short)} with root {root}: " +
                    $"{stagedAccounts:N0} accounts and {stagedSlots:N0} slots in {importing.Elapsed:hh\\:mm\\:ss}.");
            return root;
        }
        finally { File.Delete(copyPath); }
    }

    private static void RecoverStaging(IColumnsDb<PbtColumns> database, PbtImageAnchor anchor, CancellationToken token)
    {
        byte[] key = "migrationPreparedAnchor"u8.ToArray();
        byte[] provenance = Provenance(anchor);
        IDb metadata = database.GetColumnDb(PbtColumns.Metadata);
        byte[]? prepared = metadata.Get(key);
        if (prepared is not null)
        {
            if (!prepared.AsSpan().SequenceEqual(provenance))
                throw new InvalidDataException("Unpublished native anchor belongs to another bootstrap source.");
            foreach (PbtColumns column in database.ColumnKeys)
            {
                IDb records = database.GetColumnDb(column);
                List<byte[]> keys = new(4096);
                foreach (byte[] recordKey in records.GetAllKeys())
                {
                    token.ThrowIfCancellationRequested();
                    if (column == PbtColumns.Metadata && recordKey.AsSpan().SequenceEqual(key)) continue;
                    keys.Add(recordKey);
                    if (keys.Count == 4096) Delete();
                }
                Delete();
                records.SyncWal();
                void Delete()
                {
                    using IWriteBatch batch = records.StartWriteBatch();
                    foreach (byte[] recordKey in keys) batch.Remove(recordKey);
                    keys.Clear();
                }
            }
        }
        else
        {
            foreach (PbtColumns column in database.ColumnKeys)
                foreach (byte[] recordKey in database.GetColumnDb(column).GetAllKeys())
                    if (column != PbtColumns.Metadata || !PbtRocksDbPersistence.IsSchemaStamp(recordKey))
                        throw new InvalidDataException("Populated native anchor has no matching prepared provenance.");
            metadata.Set(key, provenance);
            metadata.SyncWal();
        }
    }

    /// <summary>Consumes both runs and returns the owned union, <paramref name="later"/>'s slots winning.</summary>
    private static ISlotRun MergeInto(ISlotRun earlier, ISlotRun later)
    {
        ISlotRun merged = earlier;
        for (int index = 0; index < SlotRun.Width; index++)
        {
            if ((later.Mask & (1 << index)) == 0) continue;
            ISlotRun next = merged.With(index, later.Get(index));
            SlotRun.Return(merged);
            merged = next;
        }
        SlotRun.Return(later);
        return merged;
    }

    private sealed class LogicalBatch(PbtRocksDbPersistence target) : IDisposable
    {
        private IPbtPersistence.IWriteBatch? _batch;
        private int _count;
        public IPbtPersistence.IWriteBatch Next()
        {
            if (_count == BatchSize) Flush();
            _count++;
            return _batch ??= target.CreateStagingWriteBatch(WriteFlags.None);
        }
        /// <summary>Commits the staged writes so a reader created afterwards sees them.</summary>
        public void Flush()
        {
            if (_batch is null) return;
            Commit();
            _batch.Dispose();
            _batch = null;
            _count = 0;
        }
        public void Commit() => _batch?.Commit();
        public void Dispose() => _batch?.Dispose();
    }
}
