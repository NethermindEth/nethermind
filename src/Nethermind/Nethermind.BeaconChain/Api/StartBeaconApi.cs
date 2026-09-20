// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api.Steps;
using Nethermind.Logging;

namespace Nethermind.BeaconChain.Api;

/// <summary>
/// Starts the Beacon API host when <see cref="IBeaconApiConfig.Enabled"/> is set. Independent of
/// <see cref="StartBeaconChain"/>: the API surface has no dependency on the engine RPC module chain
/// or on the sync driver being enabled at all.
/// </summary>
public class StartBeaconApi(IBeaconApiConfig config, BeaconApiHost host, ILogManager logManager) : IStep
{
    public async Task Execute(CancellationToken cancellationToken)
    {
        if (!config.Enabled)
        {
            return;
        }

        ILogger logger = logManager.GetClassLogger<StartBeaconApi>();
        // Bind failures (e.g. the port is already taken) must surface at startup, not disappear into
        // a fire-and-forget task the way the long-running sync driver's Start() does.
        await host.StartAsync(cancellationToken);
        if (logger.IsInfo) logger.Info("Beacon API has been enabled and started");
    }
}
