// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Api.Steps;
using Nethermind.Init.Steps;
using Nethermind.Logging;
using Nethermind.State.Pbt.Persistence;

namespace Nethermind.State.Pbt.Steps;

/// <summary>A one-shot step that reports what the persisted PBT columns hold.</summary>
/// <remarks>
/// Runs before the blockchain is initialized so nothing writes to the columns while the sweep reads them.
/// </remarks>
[StepCommand("scan-pbt", "Report what the persisted PBT columns hold.")]
[RunnerStepDependencies(typeof(InitializeBlockTree), typeof(StartMonitoring))]
public class ScanPbtTree(
    PbtScanner scanner,
    IPbtPersistence persistence,
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
            return;
        }

        if (_logger.IsInfo) _logger.Info($"Scanning the PBT database at persisted state {state}");

        PbtScanReport report = await scanner.Scan(cancellationToken);

        if (_logger.IsInfo) _logger.Info(report.Format());
    }
}
