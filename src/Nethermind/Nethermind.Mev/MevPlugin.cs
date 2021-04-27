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

using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using System.Collections.Generic;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Evm.Tracing;
using Nethermind.Facade;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.Mev.Source;
using Nethermind.TxPool;

namespace Nethermind.Mev
{
    public class MevPlugin : INethermindPlugin
    {
        private IMevConfig? _mevConfig;
        private ILogger? _logger;
        private INethermindApi _nethermindApi = null!;
        private IBundlePool? _bundlePool;

        public string Name => "MEV";

        public string Description => "Flashbots MEV spec implementation";

        public string Author => "Nethermind";

        public Task Init(INethermindApi? nethermindApi)
        {
            _nethermindApi = nethermindApi ?? throw new ArgumentNullException(nameof(nethermindApi));
            _mevConfig = _nethermindApi.Config<IMevConfig>();
            _logger = _nethermindApi.LogManager.GetClassLogger();
            if (_mevConfig.Enabled)
            {
                _bundlePool = new BundlePool(_nethermindApi.FinalizationManager);
            }

            return Task.CompletedTask;
        }

        public Task InitNetworkProtocol()
        {
            return Task.CompletedTask;
        }

        public Task InitRpcModules()
        {
            if (_mevConfig!.Enabled) 
            {   
                (IApiWithNetwork getFromApi, _) = _nethermindApi!.ForRpc;
                IJsonRpcConfig rpcConfig = getFromApi.Config<IJsonRpcConfig>();
                
                MevModuleFactory mevModuleFactory = new(
                    _mevConfig!, 
                    rpcConfig, 
                    _bundlePool!, 
                    getFromApi.BlockTree!,
                    getFromApi.DbProvider!, 
                    getFromApi.ReadOnlyTrieStore!,
                    getFromApi.BlockPreprocessor!,
                    getFromApi.StateReader!,
                    getFromApi.SpecProvider!, 
                    getFromApi.LogManager!,
                    getFromApi.ChainSpec!.ChainId);
                
                getFromApi.RpcModuleProvider!.RegisterBoundedByCpuCount(mevModuleFactory, rpcConfig.Timeout);

                if (getFromApi.TxPool != null)
                {
                    getFromApi.TxPool.NewPending += TxPoolOnNewPending;
                }

                if (_logger!.IsInfo) _logger.Info("Flashbots RPC plugin enabled");
            } 
            else 
            {
                if (_logger!.IsWarn) _logger.Info("Skipping Flashbots RPC plugin");
            }

            return Task.CompletedTask;
        }

        private void TxPoolOnNewPending(object? sender, TxEventArgs e)
        {
            IBlockchainBridge bridge = _nethermindApi!.CreateBlockchainBridge();
            // create a bundle
            // submit the bundle to Flashbots MEV-Relay
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}
