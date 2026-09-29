// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api.Steps;
using Nethermind.Init.Steps;
using Nethermind.Logging;

namespace Nethermind.Eez.Follower;

/// <summary>Starts the EEZ driver once the blockchain and the engine it drives are initialized.</summary>
/// <remarks>
/// <see cref="EezDriver"/> runs in the background and handles its own failures; the service stopper stops it with the
/// other services, before anything is disposed.
/// </remarks>
[RunnerStepDependencies(typeof(InitializeBlockchain), typeof(InitializeBlockProducer), typeof(InitializeNetwork))]
public class StartEezDriver(EezDriver driver, ILogManager logManager) : IStep
{
    public Task Execute(CancellationToken cancellationToken)
    {
        _ = driver.Start();

        ILogger logger = logManager.GetClassLogger<StartEezDriver>();
        if (logger.IsInfo) logger.Info("EEZ driver started");

        return Task.CompletedTask;
    }
}
