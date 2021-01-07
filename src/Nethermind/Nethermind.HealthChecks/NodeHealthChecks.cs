//  Copyright (c) 2020 Demerzel Solutions Limited
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

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.JsonRpc.Modules.Net;

namespace Nethermind.HealthChecks
{
    public class NodeHealthCheck: IHealthCheck
    {
        private readonly IRpcModuleProvider _rpcModuleProvider;
        public NodeHealthCheck(IRpcModuleProvider rpcModuleProvider)
        {
            _rpcModuleProvider = rpcModuleProvider ?? throw new ArgumentNullException(nameof(rpcModuleProvider));
        }

        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {            
            IEthModule ethModule = (IEthModule) await _rpcModuleProvider.Rent("eth_syncing", false);
            INetModule netModule = (INetModule) await _rpcModuleProvider.Rent("net_peerCount", false);
            try
            {
                long netPeerCount = (long) netModule.net_peerCount().GetData();
                SyncingResult ethSyncing = (SyncingResult) ethModule.eth_syncing().GetData();

                if (ethSyncing.IsSyncing == false && netPeerCount > 0)
                {
                    return HealthCheckResult.Healthy(description: $"The node is now fully synced with a network, number of peers: {netPeerCount}");
                }
                else if (ethSyncing.IsSyncing == false && netPeerCount == 0)
                {
                    return HealthCheckResult.Unhealthy(description: $"The node has 0 peers connected");
                }

                return HealthCheckResult.Unhealthy(description: $"The node is still syncing, CurrentBlock: {ethSyncing.CurrentBlock}, HighestBlock: {ethSyncing.HighestBlock}, Peers: {netPeerCount}");
            }
            catch (Exception ex)
            {
                return new HealthCheckResult(context.Registration.FailureStatus, exception: ex);
            }
            finally 
            {
                _rpcModuleProvider.Return("eth_syncing", ethModule);
                _rpcModuleProvider.Return("net_peerCount", netModule);
            }
        }
    }
}
