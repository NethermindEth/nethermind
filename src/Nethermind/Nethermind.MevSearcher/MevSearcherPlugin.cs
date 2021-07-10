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
using System.Net.Http;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.MevSearcher.Data;

namespace Nethermind.MevSearcher
{
    public class MevSearcherPlugin : INethermindPlugin
    {
        private IMevSearcherConfig _mevSearcherConfig = null!;
        private INethermindApi _nethermindApi = null!;
        private ILogger _logger = null!;
        private HttpClient _client = null!;

        private IBundleStrategy _bundleStrategy = null!;
        private IBundleSender _bundleSender = null!;
        
        public string Name => "MEV Searcher";

        public string Description => "Flashbots searcher and bundle sender";

        public string Author => "Nethermind";
        
        public bool Enabled => _mevSearcherConfig.Enabled;

        public Task Init(INethermindApi nethermindApi)
        {
            _nethermindApi = nethermindApi ?? throw new ArgumentNullException(nameof(nethermindApi));
            _mevSearcherConfig = _nethermindApi.Config<IMevSearcherConfig>();
            _logger = _nethermindApi.LogManager.GetClassLogger();

            return Task.CompletedTask;
        }

        private void ProcessIncomingTransaction(object sender, TxPool.TxEventArgs e)
        {
            Transaction transaction = e.Transaction;

            if (_bundleStrategy.ProcessTransaction(transaction, out MevBundle bundle))
            {
                _bundleSender.SendBundle(bundle, _mevSearcherConfig.Endpoint);
            }
        }

        public Task InitNetworkProtocol()
        {
            return Task.CompletedTask;
        }

        public Task InitRpcModules()
        {
            if (Enabled)
            {
                _client = new HttpClient();

                ITracer tracer = new Tracer(_nethermindApi.StateProvider!, _nethermindApi.BlockchainProcessor!);
            
                _bundleStrategy = new BundleStrategy(
                    _nethermindApi.StateProvider, 
                    _nethermindApi.EngineSigner, 
                    tracer, 
                    _nethermindApi.BlockTree, 
                    _nethermindApi.SpecProvider,
                    _nethermindApi.EthereumEcdsa);
                _bundleSender = new BundleSender(_client, _nethermindApi.EngineSigner, _logger);
                
                _nethermindApi.TxPool!.NewPending += ProcessIncomingTransaction;
                if (_logger!.IsInfo) _logger.Info("Flashbots searcher plugin enabled");
            }
            else
            {
                if (_logger!.IsWarn) _logger.Info("Skipping Flashbots searcher plugin");
            }

            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}
