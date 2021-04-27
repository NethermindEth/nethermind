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

using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Rewards;
using Nethermind.Blockchain.Tracing;
using Nethermind.Blockchain.Validators;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.Mev.Execution;
using Nethermind.Mev.Source;
using Nethermind.State;
using Nethermind.Trie.Pruning;

namespace Nethermind.Mev
{
    public class MevModuleFactory : ModuleFactoryBase<IMevRpcModule>
    {
        private readonly IMevConfig _mevConfig;
        private readonly IJsonRpcConfig _jsonRpcConfig;
        private readonly IBundlePool _bundlePool;
        private readonly IBlockTree _blockTree;
        private readonly IDbProvider _dbProvider;
        private readonly IReadOnlyTrieStore _trieStore;
        private readonly IBlockPreprocessorStep _recoveryStep;
        private readonly IStateReader _stateReader;
        private readonly ISpecProvider _specProvider;
        private readonly ILogManager _logManager;
        private readonly ulong _chainId;

        public MevModuleFactory(
            IMevConfig mevConfig, 
            IJsonRpcConfig jsonRpcConfig,
            IBundlePool bundlePool, 
            IBlockTree blockTree, 
            IDbProvider dbProvider,
            IReadOnlyTrieStore trieStore,
            IBlockPreprocessorStep recoveryStep,
            IStateReader stateReader,
            ISpecProvider specProvider,
            ILogManager logManager,
            ulong chainId)

        {
            _mevConfig = mevConfig;
            _jsonRpcConfig = jsonRpcConfig;
            _bundlePool = bundlePool;
            _blockTree = blockTree;
            _dbProvider = dbProvider;
            _trieStore = trieStore;
            _recoveryStep = recoveryStep;
            _stateReader = stateReader;
            _specProvider = specProvider;
            _logManager = logManager;
            _chainId = chainId;
        }
        
        public override IMevRpcModule Create()
        {
            TracerFactory tracerFactory = new(_dbProvider, _blockTree, _trieStore, _recoveryStep, _specProvider, _logManager);
            
            return new MevRpcModule(
                _mevConfig,
                _jsonRpcConfig, 
                _bundlePool,
                _blockTree,
                _stateReader, 
                tracerFactory, 
                _chainId);
        }
    }
}
