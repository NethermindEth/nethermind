// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Pbt;
using Nethermind.State.Flat;
using Nethermind.State.Pbt.Persistence;

namespace Nethermind.State.Pbt.Image;

/// <summary>Imports EIP-8347 artifacts as the native PBT state at their anchor block.</summary>
/// <remarks>
/// The provenance marker written before staging makes the import idempotent: a restart with the same source takes
/// the fast path without re-verifying the whole image, a restart with another source is refused, and an interrupted
/// staging is wiped and redone.
/// </remarks>
public sealed class PbtAnchorImport(
    PbtRocksDbPersistence target,
    IColumnsDb<PbtColumns> targetDb,
    PbtPersistenceManager persistenceManager,
    IBlockTree blockTree,
    PbtLeafIngestion leafIngestion,
    IPbtConfig config,
    ILogManager logManager)
{
    private static readonly byte[] _provenanceKey = "migrationPreparedAnchor"u8.ToArray();

    /// <summary>Which anchor a native PBT database was seeded from, so a restart against a different one is refused.</summary>
    private sealed record AnchorProvenance(string ChainId, string GenesisHash, string AnchorHash, long AnchorNumber, string AnchorMptRoot);

    private static byte[] Provenance(PbtImageAnchor anchor) => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
        new AnchorProvenance(anchor.ChainId, anchor.GenesisHash.ToString(), anchor.Header.Hash!.ToString(),
            (long)anchor.Header.Number, anchor.Header.StateRoot!.ToString()));
    private readonly ILogger _logger = logManager.GetClassLogger<PbtAnchorImport>();

    /// <summary>Imports the native PBT state at the anchor from a snapshot, optionally verified by preimages.</summary>
    /// <remarks>The preimages are not ingested: joined against the snapshot's leaves as they stream, they only rebuild
    /// the anchor's MPT root over the snapshot's state. Without them nothing ties the snapshot to the anchor's MPT root;
    /// only its own claimed PBT root is checked. Preimages are verified before the fold publishes the tree, which is
    /// what marks the target valid, so a refused import stays unpublished and a restart wipes its staging.</remarks>
    public async Task ImportSnapshot(Stream snapshot, Stream? preimages,
        PbtImageAnchor anchor, string scratchDirectory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        anchor.Validate();
        Stopwatch importing = Stopwatch.StartNew();
        if (ImportedEarlier(anchor)) return;

        ValueHash256 claimedRoot = PbtSnapshotCodec.ReadRoot(snapshot);
        if (!blockTree.IsMainChain(anchor.Header)) throw new InvalidOperationException("Migration anchor changed before the import.");
        RecoverStaging(anchor, cancellationToken);
        using PbtImageVerifier? verifier = preimages is null ? null : new(scratchDirectory, config.ImportSortBufferBytes, logManager, cancellationToken);
        verifier?.SpoolPreimages(preimages!);
        (ValueHash256 root, ulong accounts, ulong slots) = await leafIngestion.Ingest(Leaves, new StateId(anchor.Header), claimedRoot, VerifyStaged, cancellationToken);
        Finish(anchor, root, accounts, slots, importing, cancellationToken);

        IEnumerable<RebuildEntry> Leaves(CancellationToken token)
        {
            snapshot.Position = 0;
            IEnumerable<RebuildEntry> leaves = PbtSnapshotCodec.ReadLeaves(snapshot, token);
            return verifier is null ? leaves : verifier.Join(leaves);
        }

        void VerifyStaged(CancellationToken token)
        {
            if (verifier is null) return;
            verifier.Verify(anchor, token);
            if (!blockTree.IsMainChain(anchor.Header)) throw new InvalidOperationException("Migration anchor or MPT state changed during verification.");
        }
    }

    /// <summary>Whether this anchor was already imported.</summary>
    private bool ImportedEarlier(PbtImageAnchor anchor)
    {
        if (!target.IsValid || !targetDb.GetColumnDb(PbtColumns.Metadata).TryGet(_provenanceKey, static (prepared, provenance) =>
            {
                if (!prepared.SequenceEqual(provenance))
                    throw new InvalidDataException("The native PBT database was imported from another migration source.");
            }, Provenance(anchor))) return false;
        if (_logger.IsInfo) _logger.Info($"PBT migration anchor {anchor.Header.ToString(BlockHeader.Format.Short)} was imported earlier; reusing it.");
        return true;
    }

    private void Finish(PbtImageAnchor anchor, in ValueHash256 root, ulong stagedAccounts, ulong stagedSlots, Stopwatch importing, CancellationToken cancellationToken)
    {
        foreach (PbtColumns column in targetDb.ColumnKeys) targetDb.GetColumnDb(column).SyncWal();
        cancellationToken.ThrowIfCancellationRequested();

        // The import bypassed the live persistence: reload what it cached before the write.
        persistenceManager.ResetPersistedStateId();
        if (_logger.IsInfo)
            _logger.Info($"Imported the PBT migration anchor {anchor.Header.ToString(BlockHeader.Format.Short)} with root {root}: " +
                $"{stagedAccounts:N0} accounts and {stagedSlots:N0} slots in {importing.Elapsed:hh\\:mm\\:ss}.");
    }

    private void RecoverStaging(PbtImageAnchor anchor, CancellationToken token)
    {
        byte[] provenance = Provenance(anchor);
        IDb metadata = targetDb.GetColumnDb(PbtColumns.Metadata);
        if (metadata.TryGet(_provenanceKey, static (prepared, provenance) =>
            {
                if (!prepared.SequenceEqual(provenance))
                    throw new InvalidDataException("Unpublished native anchor belongs to another bootstrap source.");
            }, provenance))
        {
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
}
