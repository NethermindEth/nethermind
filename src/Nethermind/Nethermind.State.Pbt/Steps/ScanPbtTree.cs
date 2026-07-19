// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Api.Steps;
using Nethermind.Config;
using Nethermind.Init.Steps;
using Nethermind.Logging;
using Nethermind.State.Pbt.Persistence;

namespace Nethermind.State.Pbt.Steps;

/// <summary>
/// A one-shot step that reports what the persisted PBT columns hold — the trie's shape by depth and
/// what each of the store's space optimizations elides — then exits the process.
/// </summary>
/// <remarks>
/// Runs before the blockchain is initialized, as <see cref="ImportPbtFromPreimageFlat"/> does, so
/// nothing writes to the columns while the sweep reads them.
/// </remarks>
[RunnerStepDependencies(
    dependencies: [typeof(InitializeBlockTree)],
    dependents: [typeof(InitializeBlockchain)]
)]
public class ScanPbtTree(
    PbtScanner scanner,
    IPbtPersistence persistence,
    IProcessExitSource exitSource,
    ILogManager logManager
) : IStep
{
    private readonly ILogger _logger = logManager.GetClassLogger<ScanPbtTree>();

    public async Task Execute(CancellationToken cancellationToken)
    {
        StateId state;
        using (IPbtPersistence.IReader reader = persistence.CreateReader()) state = reader.CurrentState;

        if (state == StateId.PreGenesis)
        {
            if (_logger.IsInfo) _logger.Info("The PBT database holds no persisted state; nothing to scan.");
            exitSource.Exit(0);
            return;
        }

        // only what the coordinator has written is visible here; snapshots it still holds are not
        if (_logger.IsInfo) _logger.Info($"Scanning the PBT database at persisted state {state}");

        PbtScanReport report;
        try
        {
            report = await scanner.Scan(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (_logger.IsInfo) _logger.Info("PBT scan cancelled.");
            exitSource.Exit(1);
            return;
        }

        if (_logger.IsInfo) _logger.Info(report.Format());

        // every stored node caches its subtree's stem count, so the root's is an independent check on the sweep
        if (!report.StemCountAgrees && _logger.IsWarn)
        {
            _logger.Warn($"PBT scan counted {report.StemCount:N0} stems, but the root node records {report.RootSubtreeStemCount:N0} for its subtree.");
        }

        exitSource.Exit(0);
    }
}
