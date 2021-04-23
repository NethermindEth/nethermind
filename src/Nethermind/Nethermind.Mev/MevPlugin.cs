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
using System.Numerics;
using System.Threading.Tasks;
using System.Collections.Generic;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.Core;

namespace Nethermind.Mev
{
    public class MevPlugin : INethermindPlugin
    {
        private INethermindApi? _nethermindApi;

        public INethermindApi NethermindApi 
        { 
            get { ThrowIfNotInitialized(); return _nethermindApi!; } 
        }

        private IMevConfig? _mevConfig;

        private ILogger? _logger;

        private List<MevBundleForRpc> _mevBundles = new List<MevBundleForRpc>();

        public List<MevBundleForRpc> MevBundles 
        { 
            get { ThrowIfNotInitialized(); return _mevBundles!; } 
        }

        private readonly object _locker = new();

        public string Name => "MEV";

        public string Description => "Flashbots MEV spec implementation";

        public string Author => "Nethermind";

        public Task Init(INethermindApi? nethermindApi)
        {
            _nethermindApi = nethermindApi ?? throw new ArgumentNullException(nameof(nethermindApi));
            _mevConfig = nethermindApi.Config<IMevConfig>();
            _logger = _nethermindApi.LogManager.GetClassLogger();
            return Task.CompletedTask;
        }

        public Task InitNetworkProtocol()
        {
            return Task.CompletedTask;
        }

        public Task InitRpcModules()
        {
            ThrowIfNotInitialized();
            if (_mevConfig!.Enabled) 
            {   
                (IApiWithNetwork getFromApi, _) = _nethermindApi!.ForRpc;
                IJsonRpcConfig rpcConfig = getFromApi.Config<IJsonRpcConfig>();
                MevModuleFactory mevModuleFactory = new(_mevConfig!, rpcConfig, this);
                getFromApi.RpcModuleProvider!.RegisterBoundedByCpuCount(mevModuleFactory, rpcConfig.Timeout);

                if (_logger!.IsInfo) _logger.Info("Flashbots RPC plugin enabled");
            } 
            else 
            {
                if (_logger!.IsWarn) _logger.Info("Skipping Flashbots RPC plugin");
            }

            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }

        private void ThrowIfNotInitialized()
        {
            if (_nethermindApi is null || _mevConfig is null || _logger is null)
            {
                throw new InvalidOperationException($"{nameof(MevPlugin)} not yet initialized");
            }
        }

        // TODO alias nested list
        public List<List<Transaction>> GetCurrentMevTxBundles(BigInteger blockNumber, BigInteger blockTimestamp) {
            ThrowIfNotInitialized();
            lock (_locker) 
            {
                var currentAndFutureMevBundles = new List<MevBundleForRpc>();
                var currentTxBundles = new List<List<Transaction>>();

                foreach (var mevBundle in _mevBundles!) 
                {
                    if ((mevBundle.MaxTimestamp != 0 && blockTimestamp > mevBundle.MaxTimestamp) || (blockNumber > mevBundle.BlockNumber)) continue;

                    if ((mevBundle.MinTimestamp != 0 && blockTimestamp < mevBundle.MinTimestamp) || (blockNumber < mevBundle.BlockNumber)) 
                    {
                        currentAndFutureMevBundles.Add(mevBundle);
                        continue;
                    }

                    currentTxBundles.Add(mevBundle.Transactions);
                    currentAndFutureMevBundles.Add(mevBundle);
                }

                _mevBundles = currentAndFutureMevBundles;
                return currentTxBundles;
            }
        }

        public void AddMevBundle(MevBundleForRpc mevBundle) 
        {
            ThrowIfNotInitialized();
            lock (_locker) 
            {
                _mevBundles!.Add(mevBundle);
            }
        }
    }
}
