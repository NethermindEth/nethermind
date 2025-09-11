// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api.Steps;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Logging;

namespace Nethermind.Init.Steps;

[RunnerStepDependencies(typeof(InitializeBlockchain))]
public class ExitOnInvalidBlock(
    IMainProcessingContext mainProcessingContext,
    IProcessExitSource processExitSource,
    ILogManager logManager
) : IStep
{
    public Task Execute(CancellationToken cancellationToken)
    {
        mainProcessingContext.BlockchainProcessor.InvalidBlock += (sender, args) =>
        {
            logManager.GetClassLogger<ExitOnInvalidBlock>().Info("Exiting on invalid block");
            processExitSource.Exit(-1);
        };

        return Task.CompletedTask;
    }
}
