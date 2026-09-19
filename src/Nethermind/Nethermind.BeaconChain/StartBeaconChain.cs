// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api.Steps;
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
/// <see cref="BeaconChainService"/> is a long-running background service; it is started
/// fire-and-forget here and disposed by the container.
/// </remarks>
[RunnerStepDependencies(typeof(RegisterRpcModules))]
public class StartBeaconChain(BeaconChainService service, ILogManager logManager) : IStep
{
    public Task Execute(CancellationToken cancellationToken)
    {
        _ = service.Start(); // NOTE: Fire and forget, exception handling must be done inside `Start`

        ILogger logger = logManager.GetClassLogger<StartBeaconChain>();
        if (logger.IsInfo) logger.Info("Embedded beacon chain driver has been enabled and started");

        return Task.CompletedTask;
    }
}
