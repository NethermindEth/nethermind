// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Api.Steps;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Init.Steps;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State.Pbt.Image;
using Nethermind.State.Pbt.Migration;

namespace Nethermind.State.Pbt.Steps;

/// <summary>Imports the EIP-8347 snapshot at <see cref="IPbtConfig.MigrationAnchor"/> as the PBT state.</summary>
/// <remarks>
/// Diagnostic: the node then keeps running on PBT from the anchor, reporting each child header's state root.
/// Runs before <see cref="ReviewBlockTree"/> so the startup fixer finds the imported state. A restart with the
/// same snapshot reuses the import.
/// </remarks>
[RunnerStepDependencies(dependencies: [typeof(LoadGenesisBlock), typeof(StartMonitoring)], dependents: [typeof(ReviewBlockTree), typeof(InitializeNetwork)])]
internal sealed class ImportMigrationSnapshotWithFakeRoots(
    PbtAnchorPublication publication,
    IBlockTree blockTree,
    ChainSpec chainSpec,
    IDbFactory dbFactory,
    IPbtConfig config) : IStep
{
    public async Task Execute(CancellationToken cancellationToken)
    {
        BlockHeader genesis = blockTree.Genesis ?? throw new InvalidDataException("Snapshot import requires an initialized genesis.");
        BlockHeader anchor = blockTree.FindHeader((ulong)config.MigrationAnchor!.Value, BlockTreeLookupOptions.RequireCanonical)
            ?? throw new InvalidDataException($"Snapshot anchor {config.MigrationAnchor} is not in the canonical chain.");
        string scratch = Path.Combine(dbFactory.GetFullDbPath(new DbSettings("migration-work", "migration-work")), "bootstrap");

        await using FileStream snapshot = File.Open(config.MigrationSnapshotPath!, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using FileStream preimages = File.Open(config.MigrationPreimagesPath!, FileMode.Open, FileAccess.Read, FileShare.Read);
        await publication.PublishSnapshot(snapshot, preimages, PbtMigrationAnchor.Create(chainSpec, genesis, anchor), scratch,
            () => blockTree.IsMainChain(anchor), cancellationToken);
    }
}
