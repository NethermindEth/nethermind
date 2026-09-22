// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Pbt.Image;
using Nethermind.State.Pbt.Persistence;

namespace Nethermind.State.Pbt.Migration;

/// <summary>Seeds the native PBT database at a finalized pre-activation anchor, or exports the anchor artifacts.</summary>
/// <remarks>
/// Runs after the MPT genesis is loaded. Flat is live, so it may be ahead of or behind the anchor: the BAL follower
/// brings PBT up to the head, and <see cref="MigrationBackendSelector"/> keeps flat alone while a branch is below a
/// PBT pointer that got ahead of it.
/// </remarks>
internal sealed class PbtMigrationBootstrap(
    IDbFactory dbFactory,
    IDbProvider dbProvider,
    IPersistence flatPersistence,
    IColumnsDb<PbtColumns> pbtDatabase,
    IPbtPersistence pbtPersistence,
    IPbtDbManager pbtManager,
    PbtAnchorPublication publication,
    IPbtConfig configuration,
    IInitConfig initConfiguration,
    ChainSpec chainSpec,
    IBlockTree blockTree,
    ILogManager logManager,
    MigrationGenesisBootstrap? genesisBootstrap = null)
{
    private readonly ILogger _logger = logManager.GetClassLogger<PbtMigrationBootstrap>();

    /// <returns>True when the run only exported artifacts and the process should exit.</returns>
    public async Task<bool> Initialize(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BlockHeader genesis = blockTree.Genesis ?? throw new InvalidDataException("Migration requires initialized MPT genesis.");
        if (genesis.Hash is null || initConfiguration.GenesisHash is { } expectedGenesis && new Hash256(expectedGenesis) != genesis.Hash)
            throw new InvalidDataException("Configured genesis hash differs from the loaded chain.");

        if (!HasSource())
        {
            VerifyAlignment();
            return false;
        }

        genesisBootstrap?.EnsureSource(cancellationToken);
        using RuntimeBootstrapLease lease = CreateLease(genesis);
        if (configuration.MigrationExportPath is { } outputPath)
        {
            PbtOfflineExport.Export(lease, outputPath, logManager, cancellationToken);
            return true;
        }
        await Import(lease, cancellationToken);
        VerifyAlignment();
        return false;
    }

    private bool HasSource() => configuration.MigrationSnapshotPath is not null || configuration.MigrationPreimageSourcePath is not null || configuration.MigrationGenesisBootstrap;

    private async Task Import(PbtBootstrapLease lease, CancellationToken cancellationToken)
    {
        BlockHeader header = lease.Anchor.Header;
        if (header.StateRoot is null || header.Hash is null || !lease.Anchor.IsFinalized || header.Timestamp >= lease.Anchor.ActivationTimestamp)
            throw new InvalidDataException("Migration requires a trusted finalized pre-activation anchor.");
        if (lease.MptAnchor.IsPreimageMode)
            throw new InvalidDataException("Migration requires a standard-flat target; preimage-flat is an offline source only.");
        if (!lease.IsAnchorCurrent()) throw new InvalidOperationException("Migration anchor is no longer available.");
        if (lease.Snapshot is { } snapshot && lease.Preimages is { } preimages)
        {
            if (lease.OfflineSource is not null || lease.OfflineCode is not null)
                throw new InvalidDataException("Migration bootstrap has more than one source.");
            await publication.Publish(snapshot, preimages, lease.Identity, lease.Anchor, lease.ScratchDirectory, lease.IsAnchorCurrent, cancellationToken);
            return;
        }
        if (lease.Snapshot is not null || lease.Preimages is not null || lease.OfflineSource is null || lease.OfflineCode is null)
            throw new InvalidDataException("Migration bootstrap requires a portable pair or an immutable offline source and code store.");

        Directory.CreateDirectory(lease.ScratchDirectory);
        string directory = Path.Combine(lease.ScratchDirectory, $"pbt-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await using FileStream exportedSnapshot = new(Path.Combine(directory, "snapshot.pbt"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            await using FileStream exportedPreimages = new(Path.Combine(directory, "preimages.bin"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            await using FileStream manifest = new(Path.Combine(directory, "manifest.json"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            PbtOfflineSource.WriteArtifacts(lease.OfflineSource, lease.OfflineCode, lease.Identity, lease.Anchor,
                directory, exportedSnapshot, exportedPreimages, manifest, logManager, cancellationToken: cancellationToken);
            exportedSnapshot.Position = 0;
            exportedPreimages.Position = 0;
            await publication.Publish(exportedSnapshot, exportedPreimages, lease.Identity, lease.Anchor, lease.ScratchDirectory, lease.IsAnchorCurrent, cancellationToken);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    /// <remarks>Either backend may be ahead of the other after a crash; both are recoverable, an off-chain PBT pointer is not.</remarks>
    private void VerifyAlignment()
    {
        using IPersistence.IPersistenceReader flatReader = flatPersistence.CreateReader();
        using IPbtPersistence.IReader pbtReader = pbtPersistence.CreateReader();
        StateId pbtState = pbtReader.CurrentState;
        if (pbtState == StateId.PreGenesis && !pbtManager.HasStateForBlock(new StateId(blockTree.Genesis)))
            throw new InvalidConfigurationException("The native PBT database holds no state; configure a migration source to import the anchor.", ExitCodes.ConflictingConfigurations);
        if (pbtState != StateId.PreGenesis && blockTree.FindHeader(pbtState.BlockNumber, BlockTreeLookupOptions.RequireCanonical)?.StateRoot?.ValueHash256 != pbtState.StateRoot)
            throw new InvalidConfigurationException($"The persisted PBT state {pbtState} is not on the canonical chain; re-import the migration anchor.", ExitCodes.ConflictingConfigurations);
        if (_logger.IsInfo) _logger.Info($"EIP-8347 migration: flat state at {flatReader.CurrentState}, PBT state at {pbtState}.");
    }

    private RuntimeBootstrapLease CreateLease(BlockHeader genesis)
    {
        PbtArtifactIdentity identity;
        if (configuration.MigrationGenesisBootstrap)
            identity = new(chainSpec.ChainId.ToString(CultureInfo.InvariantCulture), genesis.Hash!.ToString(), genesis.Hash.ToString(),
                0, genesis.StateRoot!.ToString(), "f3079a09e8c606afcb0e5e1a309ff228b88dc067", "nethermind", "genesis");
        else
        {
            using FileStream manifest = File.OpenRead(configuration.MigrationManifestPath!);
            identity = PbtArtifactManifest.Read(manifest);
        }
        BlockHeader header = blockTree.FindHeader(new Hash256(identity.AnchorHash), BlockTreeLookupOptions.RequireCanonical)
            ?? throw new InvalidDataException("Migration anchor is not present in the trusted canonical chain.");
        bool IsCurrent() => blockTree.IsMainChain(header) && (header.IsGenesis || IsFinalized(header));
        if (!IsCurrent()) throw new InvalidDataException("Migration anchor is not finalized; re-anchor required.");
        PbtImageAnchor anchor = new(chainSpec.ChainId.ToString(CultureInfo.InvariantCulture), genesis.Hash!, header,
            true, chainSpec.Parameters.Eip8347TransitionTimestamp!.Value, 256 * 1024 * 1024);
        string scratch = Path.Combine(dbFactory.GetFullDbPath(new DbSettings("migration-work", "migration-work")), "bootstrap");
        return RuntimeBootstrapLease.Create(anchor, identity, flatPersistence, pbtDatabase, scratch, IsCurrent,
            configuration, genesisBootstrap?.Source, dbProvider.CodeDb, logManager);
    }

    private bool IsFinalized(BlockHeader header)
    {
        if (blockTree.FinalizedHash is not { } finalizedHash) return false;
        BlockHeader? finalized = blockTree.FindHeader(finalizedHash, BlockTreeLookupOptions.RequireCanonical);
        return finalized is not null && finalized.Number >= header.Number;
    }
}
