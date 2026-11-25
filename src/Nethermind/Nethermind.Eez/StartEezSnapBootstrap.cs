// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using System.Threading;
using Nethermind.Api.Steps;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Init.Steps;
using Nethermind.Logging;

namespace Nethermind.Eez;

[RunnerStepDependencies(typeof(PrepareEezSnapBootstrap), typeof(InitializeNetwork), typeof(InitializeBlockProducer))]
internal sealed class StartEezSnapBootstrap(
    EezSnapBootstrap bootstrap,
    StartEezDriver follower,
    ISyncConfig sync,
    IProcessExitSource exit,
    ILogManager logs) : IStep
{
    public async Task Execute(CancellationToken cancellationToken)
    {
        if (!sync.SynchronizationEnabled)
        {
            await follower.Execute(cancellationToken);
            return;
        }
        await bootstrap.VerifyDownload(cancellationToken);
        ILogger logger = logs.GetClassLogger<StartEezSnapBootstrap>();
        if (logger.IsInfo) logger.Info("EEZ SNAP download verified. Restart with Sync.SynchronizationEnabled=false and Init.ProcessingEnabled=true to install the checkpoint and start the follower.");
        exit.Exit(ExitCodes.Ok);
    }
}
