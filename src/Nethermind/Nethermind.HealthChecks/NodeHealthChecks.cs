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
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Nethermind.Blockchain.Find;
using Nethermind.JsonRpc.Client;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Reflection;
using Nethermind.JsonRpc.Modules.Eth;

namespace Nethermind.HealthChecks
{
    public class NodeHealthCheck: IHealthCheck
    {
        private readonly IJsonRpcClient _client;
        private readonly ILogger _logger;
        private readonly IRpcModuleProvider _rpcModuleProvider;

        public NodeHealthCheck(IRpcModuleProvider rpcModuleProvider)
        {
            _rpcModuleProvider = rpcModuleProvider ?? throw new ArgumentNullException(nameof(rpcModuleProvider));
        }

        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            
            try
            {
                IEthModule result = (IEthModule) await _rpcModuleProvider.Rent("eth_syncing", false);
                SyncingResult ethSyncing = (SyncingResult) result.eth_syncing().GetData();
                throw new Exception(ethSyncing.IsSyncing.ToString());                  
                return HealthCheckResult.Healthy(description: "The node is now fully synced with a network");
            }
            // finally 
            // {
            //     _rpcModuleProvider.Return()
            // }
            catch (Exception ex)
            {
                return new HealthCheckResult(context.Registration.FailureStatus, exception: ex);
            }
        }
    }
}
