// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Logging;

namespace Nethermind.Core.ServiceStopper;

public class ServiceStopper(ILogManager logManager) : IServiceStopper
{
    private readonly HashSet<IStoppableService> _stoppables = [];
    // Init steps run in parallel, so registrations can arrive from several threads at once.
    private readonly Lock _stoppablesLock = new();
    private bool _stopping;
    private readonly ILogger _logger = logManager.GetClassLogger<ServiceStopper>();


    public Task StopAllServices()
    {
        IStoppableService[] stoppables;
        lock (_stoppablesLock)
        {
            _stopping = true;
            stoppables = [.. _stoppables];
        }

        return Task.WhenAll(stoppables.Select(async (stoppable) =>
            await Task.Run( // Task run in the middle so that the log look nice.
                async () => await Stop(stoppable))));
    }

    void IServiceStopper.AddStoppable(IStoppableService stoppableService)
    {
        lock (_stoppablesLock)
        {
            if (!_stopping)
            {
                _stoppables.Add(stoppableService);
                return;
            }
        }

        // Shutdown can begin while init steps are still registering; those services would otherwise never be stopped.
        _ = Task.Run(() => Stop(stoppableService));
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
