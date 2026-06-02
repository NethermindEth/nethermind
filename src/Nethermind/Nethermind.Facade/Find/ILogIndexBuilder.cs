// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Core.ServiceStopper;

namespace Nethermind.Facade.Find
{
    public interface ILogIndexBuilder : IAsyncDisposable, IStoppableService
    {
        Task StartAsync();
        bool IsRunning { get; }

        int MaxTargetBlockNumber { get; }
        int MinTargetBlockNumber { get; }

        DateTimeOffset? LastUpdate { get; }
        Exception? LastError { get; }
    }
}
