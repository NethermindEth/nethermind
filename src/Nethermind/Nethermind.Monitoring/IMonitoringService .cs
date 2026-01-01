// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Core.ServiceStopper;

namespace Nethermind.Monitoring
{
    public interface IMonitoringService: IStoppableService
    {
        Task StartAsync();
        void AddMetricsUpdateAction(Action callback);
    }
}
