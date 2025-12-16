// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api.Steps;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Importer;
using Nethermind.State.Flat.Persistence;

namespace Nethermind.Init.Steps;

[RunnerStepDependencies(
    dependencies: [typeof(InitializeBlockTree)],
    dependents: [typeof(InitializeBlockchain)]
)]
public class ImportFlatDb(
    IBlockTree blockTree,
    IPersistence persistence,
    Importer importer,
    IProcessExitSource exitSource,
    ILogManager logManager
): IStep
{
    ILogger _logger = logManager.GetClassLogger<ImportFlatDb>();

    public Task Execute(CancellationToken cancellationToken)
    {
        BlockHeader? head = blockTree.Head?.Header;
        if (head is null) return Task.CompletedTask;

        using (var reader = persistence.CreateReader())
        {
            _logger.Warn($"Current state is {reader.CurrentState}");
            if (reader.CurrentState.blockNumber > 0)
            {
                _logger.Info("Flat db already exist");
                return Task.CompletedTask;
            }
        }

        _logger.Info($"Copying state {head.ToString(BlockHeader.Format.Short)} with state root {head.StateRoot}");
        importer.Copy(new StateId(head));

        exitSource.Exit(0);

        return Task.CompletedTask;
    }
}
