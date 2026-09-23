// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Api.Steps;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Db;
using Nethermind.Init.Steps;
using Nethermind.Logging;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Pbt.Image;
using Nethermind.State.Pbt.Migration;
using FlatStateId = Nethermind.State.Flat.StateId;

namespace Nethermind.State.Pbt.Steps;

/// <summary>A one-shot step that exports the EIP-8347 artifacts for an anchor block, then exits the process.</summary>
/// <remarks>
/// The anchor may be ahead of the persisted state, so this runs once the node is live and syncing, and waits.
/// Flat persistence is pinned to the anchor through <see cref="PbtExportPersistTarget"/> so it lands on that
/// exact block instead of the next compaction boundary and goes no further; block processing is then paused so
/// nothing accumulates underneath the export.
/// </remarks>
[RunnerStepDependencies(dependencies: [typeof(InitializeNetwork)])]
public class ExportPbtImage(
    IPersistenceManager persistenceManager,
    IPersistence flatPersistence,
    PbtExportPersistTarget persistTarget,
    IDbFactory dbFactory,
    IDbProvider dbProvider,
    IBlockTree blockTree,
    ChainSpec chainSpec,
    IPbtConfig config,
    IBlockProcessingPauseControl pauseControl,
    IProcessExitSource exitSource,
    ILogManager logManager
) : IStep
{
    private static readonly TimeSpan ReportInterval = TimeSpan.FromMinutes(1);

    /// <summary>How often the persisted state is re-read; there is no event for it to wait on.</summary>
    internal TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(5);

    private readonly ILogger _logger = logManager.GetClassLogger<ExportPbtImage>();

    public async Task Execute(CancellationToken cancellationToken)
    {
        ulong anchorNumber = ResolveAnchor();
        persistTarget.PinTo(anchorNumber);
        await WaitForPersistence(anchorNumber, cancellationToken);

        pauseControl.Pause();
        FlatStateId persisted = persistenceManager.GetCurrentPersistedStateId();
        if (persisted.BlockNumber != anchorNumber)
            throw new InvalidOperationException($"Persisted state moved to {persisted} while pinned to the export anchor {anchorNumber}.");
        BlockHeader header = blockTree.FindHeader(anchorNumber, BlockTreeLookupOptions.RequireCanonical)
            ?? throw new InvalidDataException("Export anchor is not present in the canonical chain.");

        using IPersistence.IPersistenceReader source = flatPersistence.CreateReader();
        string scratch = Path.Combine(dbFactory.GetFullDbPath(new DbSettings("migration-work", "migration-work")), "export");
        PbtOfflineExport.Export(source, dbProvider.CodeDb,
            PbtMigrationAnchor.Create(chainSpec, blockTree.Genesis!, header),
            config.MigrationExportPath!, scratch, () => blockTree.IsMainChain(header), logManager, cancellationToken);
        exitSource.Exit(0);
    }

    /// <remarks>Flat keeps no history, so a state below the persisted one cannot be reconstructed. Rather than
    /// fail late, after a long scan, refuse an anchor already behind the node.</remarks>
    private ulong ResolveAnchor()
    {
        FlatStateId persisted = persistenceManager.GetCurrentPersistedStateId();
        if (persisted == FlatStateId.PreGenesis)
        {
            if (config.MigrationAnchor is null)
                throw new InvalidConfigurationException(
                    "No flat state is persisted yet, so the export has nothing to anchor at; set Pbt.MigrationAnchor.",
                    ExitCodes.ConflictingConfigurations);
            return (ulong)config.MigrationAnchor.Value;
        }

        if (config.MigrationAnchor is not { } configured) return persisted.BlockNumber;
        if ((ulong)configured < persisted.BlockNumber)
            throw new InvalidConfigurationException(
                $"Export anchor {configured} is below the persisted flat state {persisted.BlockNumber}, which keeps no history to recover it.",
                ExitCodes.ConflictingConfigurations);
        return (ulong)configured;
    }

    private async Task WaitForPersistence(ulong anchorNumber, CancellationToken cancellationToken)
    {
        FlatStateId persisted = persistenceManager.GetCurrentPersistedStateId();
        if (persisted != FlatStateId.PreGenesis && persisted.BlockNumber == anchorNumber) return;
        if (_logger.IsInfo) _logger.Info($"Waiting for the flat state to be persisted at the EIP-8347 export anchor {anchorNumber}.");

        DateTime nextReport = DateTime.UtcNow + ReportInterval;
        while (true)
        {
            await Task.Delay(PollInterval, cancellationToken);
            persisted = persistenceManager.GetCurrentPersistedStateId();
            if (persisted != FlatStateId.PreGenesis && persisted.BlockNumber >= anchorNumber) return;
            if (DateTime.UtcNow < nextReport) continue;
            nextReport = DateTime.UtcNow + ReportInterval;
            if (_logger.IsInfo)
                _logger.Info($"EIP-8347 export waiting at persisted block {(persisted == FlatStateId.PreGenesis ? 0 : persisted.BlockNumber)} " +
                    $"of anchor {anchorNumber}; head is {blockTree.Head?.Number}.");
        }
    }
}
