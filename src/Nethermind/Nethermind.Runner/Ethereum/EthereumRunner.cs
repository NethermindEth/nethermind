// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Api;
using Nethermind.Api.Extensions;
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

        foreach (INethermindPlugin plugin in api.Plugins)
        {
            await Stop(async () => await plugin.DisposeAsync(), $"Disposing plugin {plugin.Name}");
        }

        await lifetimeScope.DisposeAsync();
        if (_logger.IsInfo)
        {
            _logger.Info("All DBs closed");
            _logger.Info("Ethereum runner stopped");
        }
    }

    private void Stop(Action stopAction, string description)
    {
        try
        {
            if (_logger.IsInfo) _logger.Info(description);

            stopAction();
        }
        catch (Exception e)
        {
            if (_logger.IsError) _logger.Error($"{description} shutdown error.", e);
        }
    }

    private Task Stop(Func<Task?> stopAction, string description)
    {
        try
        {
            if (_logger.IsInfo) _logger.Info(description);
            return stopAction() ?? Task.CompletedTask;
        }
        catch (Exception e)
        {
            if (_logger.IsError) _logger.Error($"{description} shutdown error.", e);
            return Task.CompletedTask;
        }
    }
}
