// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Api.Steps;
using Nethermind.BeaconChain.Engine;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.Init.Steps;
using Nethermind.Logging;

namespace Nethermind.BeaconChain;

/// <summary>
/// Starts the embedded consensus-layer driver. Registered only when
/// <see cref="IBeaconChainConfig.Enabled"/> is set, so <see cref="BeaconChainService"/> and its
/// component graph are guaranteed to be present in the container.
/// </summary>
/// <remarks>
/// Depends on <see cref="RegisterRpcModules"/> because the driver resolves
/// <c>IEngineRpcModule</c>, and the engine module's decorator chain (including the external
/// consensus client interceptor) is only complete once RPC module registration has run.
/// <see cref="BeaconChainService.Start"/> checks the database and any persisted anchor before it
/// returns, so a refusal fails this step; the driver it then starts runs in the background, is not
/// awaited here, and is disposed by the container.
/// </remarks>
[RunnerStepDependencies(typeof(RegisterRpcModules))]
public class StartBeaconChain(BeaconChainService service, IEngineDriver engine, ILogManager logManager) : IStep
{
    public Task Execute(CancellationToken cancellationToken)
    {
        // Fails startup rather than the node's first Gloas block if the engine driver still relies
        // on the interface's throwing default for the envelope overload.
        INewPayloadNotifier.RequireEnvelopeSupport(engine);

        // Throws synchronously on a database or anchor the driver refuses; the returned run handles its own failures.
        _ = service.Start();

        ILogger logger = logManager.GetClassLogger<StartBeaconChain>();
        if (logger.IsInfo) logger.Info("Embedded beacon chain driver has been enabled and started");

        return Task.CompletedTask;
    }
}
