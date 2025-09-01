// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api.Steps;
using Nethermind.Config;
using Nethermind.Init.Steps;
using Nethermind.Logging;
using Nethermind.TxPool;

namespace Nethermind.Hive;

[RunnerStepDependencies(true, typeof(InitializeNetwork))]
public class HiveStep(
    ITxPool txPool,
    CompositeTxGossipPolicy txGossipPolicy,
    HiveRunner hiveRunner,
    IProcessExitSource processExitSource,
    ILogManager logManager
) : IStep
{
    ILogger _logger = logManager.GetClassLogger();

    public async Task Execute(CancellationToken cancellationToken)
    {
        txPool!.AcceptTxWhenNotSynced = true;

        txGossipPolicy.Policies.Clear();

        if (_logger.IsInfo) _logger.Info("Hive is starting");

        await hiveRunner.Start(processExitSource.Token);
    }
}
