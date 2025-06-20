// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Logging;

namespace Nethermind.Core.ServiceStopper;

public class ServiceStopper(ILogManager logManager) : IServiceStopper
{
    private HashSet<IStoppableService> _stoppables = new HashSet<IStoppableService>();
    private ILogger _logger = logManager.GetClassLogger<ServiceStopper>();


    public Task StopAllServices()
    {
        return Task.WhenAll(_stoppables.Select(async (stoppable) =>
            await Task.Run( // Task run in the middle so that the log look nice.
                async () => await Stop(stoppable))));
    }

    void IServiceStopper.AddStoppable(IStoppableService stoppableService)
    {
        _stoppables.Add(stoppableService);
    }

    private async Task Stop(IStoppableService stoppableService)
    {
        try
        {
            if (_logger.IsInfo) _logger.Info($"Stopping {stoppableService.Description}");
            await stoppableService.StopAsync();
        }
        catch (Exception e)
        {
            if (_logger.IsError) _logger.Error($"{stoppableService.Description} shutdown error.", e);
        }
    }
}
