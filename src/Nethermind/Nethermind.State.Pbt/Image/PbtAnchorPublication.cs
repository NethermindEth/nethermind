// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
    private readonly ILogger _logger = logManager.GetClassLogger<PbtAnchorPublication>();

    public async Task<ValueHash256> Publish(Stream snapshot, Stream preimages, PbtArtifactIdentity identity,
        PbtImageAnchor anchor, string scratchDirectory, Func<bool> isAnchorCurrent, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[] provenance = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(identity);
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
            using PbtVerifiedImage image = PbtImageVerifier.Verify(copiedSnapshot, preimages, identity, anchor, scratchDirectory, cancellationToken);
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

            RecoverStaging(targetDb, identity, cancellationToken);
            if (target.IsValid) throw new InvalidOperationException("An unpublished populated PBT target requires recovery, not replacement.");
            using (IPbtPersistence.IReader reader = target.CreateReader())
                if (reader.CurrentState != StateId.PreGenesis)
                    throw new InvalidOperationException("PBT anchor staging must start empty.");

            using (LogicalBatch batch = new(target))
            {
                // The image lists an account's slots in hash order, so its runs complete only once the account ends.
                Dictionary<PbtStorageTreeKey, ISlotRun> runs = [];
                Address? slotsAddress = null;
                image.Replay((address, account, code) =>
                {
                    batch.Next().SetAccount(PbtKeyDerivation.AddressKeyHash(address), account);
                    if (code.Length != 0) batch.Next().SetCode(account.CodeHash.ValueHash256, new CodeInfo(code));
                }, (address, slot, value) =>
                {
                    if (address != slotsAddress) FlushRuns();
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
                    foreach ((PbtStorageTreeKey runKey, ISlotRun run) in runs)
                    {
                        batch.Next().SetSlotRun(runKey, run);
                        SlotRun.Return(run);
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
            (persistence as PbtCachedReaderPersistence)?.ClearReaderCache();
            if (_logger.IsInfo) _logger.Info($"Imported the PBT migration anchor {anchor.Header.ToString(BlockHeader.Format.Short)} with root {root}.");
            return root;
        }
        finally { File.Delete(copyPath); }
    }

    private static void RecoverStaging(IColumnsDb<PbtColumns> database, PbtArtifactIdentity identity, CancellationToken token)
    {
        byte[] key = "migrationPreparedAnchor"u8.ToArray();
        byte[] provenance = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(identity);
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

    private sealed class LogicalBatch(PbtRocksDbPersistence target) : IDisposable
    {
        private IPbtPersistence.IWriteBatch? _batch;
        private int _count;
        public IPbtPersistence.IWriteBatch Next()
        {
            if (_count == 4096) { Commit(); _batch!.Dispose(); _batch = null; _count = 0; }
            _count++;
            return _batch ??= target.CreateStagingWriteBatch(WriteFlags.None);
        }
        public void Commit() => _batch?.Commit();
        public void Dispose() => _batch?.Dispose();
    }
}
