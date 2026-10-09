// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Api.Steps;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Db;
using Nethermind.Init.Steps;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State.Pbt.Image;
using Nethermind.State.Pbt.Migration;
using Nethermind.State.Pbt.Persistence;

namespace Nethermind.State.Pbt.Steps;

/// <summary>Imports the EIP-8347 snapshot at <see cref="IPbtConfig.MigrationAnchor"/> as the PBT state.</summary>
/// <remarks>
/// Diagnostic: unlike the migration, the import runs synchronously and the PBT database becomes the node's state.
/// The snapshot is keyed by the anchor's Patricia root, so the node then runs with
/// <see cref="IPbtConfig.FakeMatchingStateRoot"/>. A PBT database that already holds state is refused.
/// </remarks>
[StepCommand("import-pbt-snapshot", "Import an EIP-8347 snapshot as the PBT state.")]
[RunnerStepDependencies(typeof(InitializeBlockTree), typeof(StartMonitoring))]
public sealed class ImportPbtSnapshot(
    PbtAnchorImport anchorImport,
    PbtRocksDbPersistence pbtPersistence,
    IBlockTree blockTree,
    ChainSpec chainSpec,
    IDbFactory dbFactory,
    IPbtConfig config) : IStep
{
    public async Task Execute(CancellationToken cancellationToken)
    {
        if (pbtPersistence.IsValid)
            throw new InvalidConfigurationException(
                "The PBT database already holds state; delete it to import a snapshot.",
                ExitCodes.ConflictingConfigurations);

        BlockHeader genesis = blockTree.Genesis ?? throw new InvalidDataException("Snapshot import requires an initialized genesis.");
        BlockHeader anchor = blockTree.FindHeader((ulong)config.MigrationAnchor!.Value, BlockTreeLookupOptions.RequireCanonical)
            ?? throw new InvalidDataException($"Snapshot anchor {config.MigrationAnchor} is not in the canonical chain.");
        string scratch = PbtMigrationAnchor.ScratchDirectory(dbFactory, "bootstrap");

        await using FileStream snapshot = File.Open(config.MigrationSnapshotPath!, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using FileStream? preimages = config.MigrationPreimagesPath is { } preimagesPath
            ? File.Open(preimagesPath, FileMode.Open, FileAccess.Read, FileShare.Read)
            : null;
        await anchorImport.ImportSnapshot(snapshot, preimages, PbtMigrationAnchor.Create(chainSpec, genesis, anchor), scratch,
            () => blockTree.IsMainChain(anchor), cancellationToken);
    }
}
