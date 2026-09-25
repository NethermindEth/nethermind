// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Api;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.ServiceStopper;
using Nethermind.Init.Steps;
using Nethermind.Logging;

namespace Nethermind.Runner.Ethereum;

public class EthereumRunner(INethermindApi api, EthereumStepsManager stepsManager, ILifetimeScope lifetimeScope, IServiceStopper serviceStopper, IProcessExitSource processExitSource)
{
    public INethermindApi Api => api;
    public ILifetimeScope LifetimeScope => lifetimeScope;
    private readonly ILogger _logger = api.LogManager.GetClassLogger<EthereumRunner>();

    public async Task Start(CancellationToken cancellationToken)
    {
        if (_logger.IsDebug) _logger.Debug("Starting Ethereum runner");

        await stepsManager.InitializeAll(cancellationToken);

        // Only reached when the target completed; anything else threw. A command run has nothing left to keep
        // the process alive, and has already produced its own output, so a node info screen would be noise.
        if (stepsManager.HasTarget)
        {
            processExitSource.Exit(ExitCodes.Ok);
            return;
        }

        string infoScreen = ThisNodeInfo.BuildNodeInfoScreen();

        if (_logger.IsInfo) _logger.Info(infoScreen);
    }

    public async Task StopAsync()
    {
        await serviceStopper.StopAllServices();

        await lifetimeScope.DisposeAsync();
        if (_logger.IsInfo)
        {
            _logger.Info("All DBs closed");
            _logger.Info("Ethereum runner stopped");
        }
    }
}
