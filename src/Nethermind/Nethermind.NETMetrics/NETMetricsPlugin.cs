//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
//
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
//
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
//
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
//

using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Logging;

namespace Nethermind.NETMetrics;

public class NETMetricsPlugin: INethermindPlugin

{
    public ValueTask DisposeAsync()
    {
        throw new NotImplementedException();
    }

    private SystemMetricsListener _metricsListener = null!;
    private INethermindApi _nethermindApi = null!;
    private ILogger? _logger;
    public string Name => ".NET Performance Monitoring Plugin";
    public string Description => "Enhance .NET-related monitoring with more counters";
    public string Author => "Nethermind";
    public Task Init(INethermindApi nethermindApi)
    {
        _nethermindApi = nethermindApi ?? throw new ArgumentNullException(nameof(nethermindApi));
        _metricsListener = new SystemMetricsListener(1);
        _logger = _nethermindApi.LogManager.GetClassLogger();
        return Task.CompletedTask;
    }

    public Task InitNetworkProtocol()
    {
        return Task.CompletedTask;
    }

    public Task InitRpcModules()
    {
        return Task.CompletedTask;
    }
}
