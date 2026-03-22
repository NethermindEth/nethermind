// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Api;
using Nethermind.Core;
using Nethermind.Core.ServiceStopper;
using Nethermind.Init.Steps;
using Nethermind.Logging;

namespace Nethermind.Runner.Ethereum;

public class EthereumRunner(INethermindApi api, EthereumStepsManager stepsManager, ILifetimeScope lifetimeScope, IServiceStopper serviceStopper)
{
    public INethermindApi Api => api;
    public ILifetimeScope LifetimeScope => lifetimeScope;
    private readonly ILogger _logger = api.LogManager.GetClassLogger();

    public async Task Start(CancellationToken cancellationToken)
    {
        if (_logger.IsDebug) _logger.Debug("Starting Ethereum runner");

        await stepsManager.InitializeAll(cancellationToken);

        string infoScreen = ThisNodeInfo.BuildNodeInfoScreen();

        if (_logger.IsInfo) _logger.Info(infoScreen);
    }

    public async Task StopAsync()
    {
        await serviceStopper.StopAllServices();

        // Time-box DB disposal so PGO data can be written even if RocksDB hangs
        // (e.g. on overlay filesystems). Will revert once snapshot disposal is fixed.
        Task disposeTask = lifetimeScope.DisposeAsync().AsTask();
        if (await Task.WhenAny(disposeTask, Task.Delay(15_000)) != disposeTask)
        {
            if (_logger.IsWarn) _logger.Warn("DB disposal timed out after 15s - proceeding with shutdown");
        }
        else
        {
            if (_logger.IsInfo) _logger.Info("All DBs closed");
        }

        if (_logger.IsInfo) _logger.Info("Ethereum runner stopped");
    }
}
