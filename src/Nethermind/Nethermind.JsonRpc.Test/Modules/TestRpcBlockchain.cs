//  Copyright (c) 2018 Demerzel Solutions Limited
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

using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain.Filters;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Facade;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Db.Blooms;
using Nethermind.Specs;
using Nethermind.TxPool;
using Nethermind.Wallet;
using Newtonsoft.Json;

namespace Nethermind.JsonRpc.Test.Modules
{
    public class TestRpcBlockchain : TestBlockchain
    {
        public IEthModule EthModule { get; private set; }
        public IBlockchainBridge Bridge { get; private set; }
        public ITxPoolBridge TxPoolBridge { get; private set; }

        protected TestRpcBlockchain(SealEngineType sealEngineType)
            : base(sealEngineType)
        {
        }

        public static Builder ForTest(SealEngineType sealEngineType)
        {
            return new Builder(sealEngineType);
        }

        public class Builder
        {
            public Builder(SealEngineType sealEngineType)
            {
                _blockchain = new TestRpcBlockchain(sealEngineType);
            }
            
            private TestRpcBlockchain _blockchain;
            
            public Builder WithBlockchainBridge(IBlockchainBridge blockchainBridge)
            {
                _blockchain.Bridge = blockchainBridge;
                return this;
            }
            
            public Builder WithTxPoolBridge(ITxPoolBridge txPoolBridge)
            {
                _blockchain.TxPoolBridge = txPoolBridge;
                return this;
            }
            
            public async Task<TestRpcBlockchain> Build()
            {
                return (TestRpcBlockchain)(await _blockchain.Build());
            }
        }

        protected override async Task<TestBlockchain> Build(ISpecProvider specProvider = null)
        {
            specProvider ??= MainnetSpecProvider.Instance;
            await base.Build(specProvider);
            IFilterStore filterStore = new FilterStore();
            IFilterManager filterManager = new FilterManager(filterStore, BlockProcessor, TxPool, LimboLogs.Instance);
            Bridge ??= new BlockchainBridge(StateReader, State, Storage, BlockTree, TxPool, ReceiptStorage, filterStore, filterManager, NullWallet.Instance, TxProcessor, EthereumEcdsa, NullBloomStorage.Instance, LimboLogs.Instance, false);
            TxPoolBridge ??= new TxPoolBridge(TxPool, NullWallet.Instance, Timestamper, specProvider?.ChainId ?? 0);
            
            EthModule = new EthModule(new JsonRpcConfig(), Bridge, TxPoolBridge, LimboLogs.Instance);
            return this;
        }

        public string TestEthRpc(string method, params string[] parameters)
        {
            return RpcTest.TestSerializedRequest(EthModuleFactory.Converters, EthModule, method, parameters);
        }

        public string TestSerializedRequest<T>(T module, string method, params string[] parameters) where T : class, IModule
        {
            return RpcTest.TestSerializedRequest(new JsonConverter[0], module, method, parameters);
        }
    }
}