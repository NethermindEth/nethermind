// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api.Steps;
using Nethermind.Init.Steps;
using Nethermind.Logging;

namespace Nethermind.Eez.Follower;

/// <summary>Starts the EEZ follower once the blockchain and the engine it drives are initialized.</summary>
/// <remarks><see cref="EezFollower"/> runs in the background and handles its own failures; the container disposes it.</remarks>
[RunnerStepDependencies(typeof(InitializeBlockchain), typeof(InitializeBlockProducer), typeof(InitializeNetwork))]
public class StartEezFollower(EezFollower follower, ILogManager logManager) : IStep
{
    public Task Execute(CancellationToken cancellationToken)
    {
        _ = follower.Start();

        ILogger logger = logManager.GetClassLogger<StartEezFollower>();
        if (logger.IsInfo) logger.Info("EEZ follower started");

        return Task.CompletedTask;
    }
}
