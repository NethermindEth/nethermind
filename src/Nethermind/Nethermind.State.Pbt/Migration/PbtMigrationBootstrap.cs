// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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

    public async Task Initialize(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BlockHeader genesis = blockTree.Genesis ?? throw new InvalidDataException("Migration requires initialized MPT genesis.");
        if (genesis.Hash is null || initConfiguration.GenesisHash is { } expectedGenesis && new Hash256(expectedGenesis) != genesis.Hash)
            throw new InvalidDataException("Configured genesis hash differs from the loaded chain.");

        if (!HasSource())
        {
            VerifyAlignment();
            return;
        }

        genesisBootstrap?.EnsureSource(cancellationToken);
        using RuntimeBootstrapLease lease = CreateLease(genesis);
        await Import(lease, cancellationToken);
        VerifyAlignment();
    }

    private bool HasSource() => configuration.MigrationSnapshotPath is not null || configuration.MigrationPreimageSourcePath is not null || configuration.MigrationGenesisBootstrap;

    private async Task Import(PbtBootstrapLease lease, CancellationToken cancellationToken)
    {
        BlockHeader header = lease.Anchor.Header;
        if (header.StateRoot is null || header.Hash is null ||
            lease.Anchor.ActivationTimestamp is { } activation && header.Timestamp >= activation)
            throw new InvalidDataException("Migration requires a trusted pre-activation anchor.");
        if (lease.MptAnchor.IsPreimageMode)
            throw new InvalidDataException("Migration requires a standard-flat target; preimage-flat is an offline source only.");
        if (!lease.IsAnchorCurrent()) throw new InvalidOperationException("Migration anchor is no longer available.");
        if (lease.Snapshot is { } snapshot && lease.Preimages is { } preimages)
        {
            if (lease.OfflineSource is not null || lease.OfflineCode is not null)
                throw new InvalidDataException("Migration bootstrap has more than one source.");
            await publication.Publish(snapshot, preimages, lease.Anchor, lease.ScratchDirectory, lease.IsAnchorCurrent, cancellationToken);
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
            PbtOfflineSource.WriteArtifacts(lease.OfflineSource, lease.OfflineCode, lease.Anchor,
                directory, exportedSnapshot, exportedPreimages, logManager,
                configuration.ExportSortBufferBytes, configuration.ExportConcurrency, cancellationToken);
            exportedSnapshot.Position = 0;
            exportedPreimages.Position = 0;
            await publication.Publish(exportedSnapshot, exportedPreimages, lease.Anchor, lease.ScratchDirectory, lease.IsAnchorCurrent, cancellationToken);
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
        ulong anchorNumber = configuration.MigrationGenesisBootstrap
            ? genesis.Number
            : (ulong)configuration.MigrationAnchor!.Value;
        BlockHeader header = blockTree.FindHeader(anchorNumber, BlockTreeLookupOptions.RequireCanonical)
            ?? throw new InvalidDataException("Migration anchor is not present in the trusted canonical chain.");
        bool IsCurrent() => blockTree.IsMainChain(header);
        PbtImageAnchor anchor = PbtMigrationAnchor.Create(chainSpec, genesis, header);
        string scratch = Path.Combine(dbFactory.GetFullDbPath(new DbSettings("migration-work", "migration-work")), "bootstrap");
        return RuntimeBootstrapLease.Create(anchor, flatPersistence, scratch, IsCurrent,
            configuration, genesisBootstrap?.Source, dbProvider.CodeDb, logManager);
    }
}
