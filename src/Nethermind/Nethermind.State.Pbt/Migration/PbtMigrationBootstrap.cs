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
using Nethermind.State.Flat;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Pbt.Image;
using Nethermind.State.Pbt.Persistence;

namespace Nethermind.State.Pbt.Migration;

/// <summary>Seeds the native PBT database at a finalized pre-activation anchor, or exports the anchor artifacts.</summary>
/// <remarks>
/// Runs after the MPT genesis is loaded. Flat is live, so it may be ahead of or behind the anchor: the BAL follower
/// brings PBT up to the head, and main processing stays on flat until activation (see <see cref="MigrationActivation.IsBinary"/>).
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

        if (!PbtMigrationConfigValidator.HasSource(configuration))
        {
            VerifyAlignment();
            return;
        }

        genesisBootstrap?.EnsureSource(cancellationToken);
        await Import(genesis, cancellationToken);
        VerifyAlignment();
    }

    /// <remarks>
    /// A snapshot is imported on its own, and preimages beside it only verify it; preimages alone take their values from
    /// the local flat state; with neither, the genesis source is imported whole. The inputs stay open through publication.
    /// </remarks>
    private async Task Import(BlockHeader genesis, CancellationToken cancellationToken)
    {
        ulong anchorNumber = configuration.MigrationGenesisBootstrap
            ? genesis.Number
            : (ulong)configuration.MigrationAnchor!.Value;
        BlockHeader header = blockTree.FindHeader(anchorNumber, BlockTreeLookupOptions.RequireCanonical)
            ?? throw new InvalidDataException("Migration anchor is not present in the trusted canonical chain.");
        bool IsCurrent() => blockTree.IsMainChain(header);
        PbtImageAnchor anchor = PbtMigrationAnchor.Create(chainSpec, genesis, header);
        string scratch = PbtMigrationAnchor.ScratchDirectory(dbFactory, "bootstrap");
        if (!IsCurrent()) throw new InvalidOperationException("Migration anchor is no longer available.");

        if (configuration.MigrationSnapshotPath is { } snapshotPath)
        {
            using Stream snapshot = File.Open(snapshotPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using Stream? verifyingPreimages = configuration.MigrationPreimagesPath is { } verifyingPath
                ? File.Open(verifyingPath, FileMode.Open, FileAccess.Read, FileShare.Read)
                : null;
            await publication.PublishSnapshot(snapshot, verifyingPreimages, anchor, scratch, IsCurrent, cancellationToken);
        }
        else if (configuration.MigrationPreimagesPath is { } preimagesPath)
        {
            using Stream preimages = File.Open(preimagesPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using IPersistence.IPersistenceReader source = flatPersistence.CreateReader();
            await publication.PublishPreimages(preimages, source, dbProvider.CodeDb, anchor, scratch, IsCurrent, cancellationToken);
        }
        else
        {
            using IPersistence.IPersistenceReader source = (genesisBootstrap?.Source
                ?? throw new InvalidOperationException("Genesis bootstrap source is not registered.")).Persistence.CreateReader();
            Directory.CreateDirectory(scratch);
            string directory = Path.Combine(scratch, $"pbt-export-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            try
            {
                await using FileStream exportedSnapshot = new(Path.Combine(directory, "snapshot.pbt"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                await using FileStream exportedPreimages = new(Path.Combine(directory, "preimages.bin"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                PbtOfflineSource.WriteArtifacts(source, dbProvider.CodeDb, anchor,
                    directory, exportedSnapshot, exportedPreimages, logManager,
                    configuration.ExportSortBufferBytes, configuration.ImportConcurrency, cancellationToken);
                exportedSnapshot.Position = 0;
                exportedPreimages.Position = 0;
                await publication.PublishSnapshot(exportedSnapshot, exportedPreimages, anchor, scratch, IsCurrent, cancellationToken);
            }
            finally { Directory.Delete(directory, recursive: true); }
        }
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
}
