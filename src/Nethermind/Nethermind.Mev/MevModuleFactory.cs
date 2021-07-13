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
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Mev.Execution;
using Nethermind.Mev.Source;
using Nethermind.State;
using Nethermind.TxPool;

namespace Nethermind.Mev
{
    public class MevModuleFactory : ModuleFactoryBase<IMevRpcModule>
    {
        private readonly IJsonRpcConfig _jsonRpcConfig;
        private readonly IBundlePool _bundlePool;
        private readonly ITxSender _txSender;
        private readonly IBlockTree _blockTree;
        private readonly IStateReader _stateReader;
        private readonly ITracerFactory _tracerFactory;
        private readonly IEciesCipher _eciesCipher;
        private readonly ISpecProvider _specProvider;
        private readonly ISigner? _signer;
        private readonly ulong _chainId;

        public MevModuleFactory(IJsonRpcConfig jsonRpcConfig,
            IBundlePool bundlePool,
            ITxSender? txSender,
            IBlockTree blockTree,
            IStateReader stateReader,
            ITracerFactory tracerFactory,
            IEciesCipher? eciesCipher,
            ISpecProvider specProvider,
            ISigner? signer,
            ulong chainId)
        {
            _jsonRpcConfig = jsonRpcConfig;
            _bundlePool = bundlePool;
            _txSender = txSender ?? throw new ArgumentNullException(nameof(txSender));
            _blockTree = blockTree;
            _stateReader = stateReader;
            _tracerFactory = tracerFactory;
            _eciesCipher = eciesCipher ?? throw new ArgumentNullException(nameof(eciesCipher));
            _specProvider = specProvider;
            _signer = signer;
            _chainId = chainId;
        }
        
        public override IMevRpcModule Create()
        {
            return new MevRpcModule(
                _jsonRpcConfig, 
                _bundlePool,
                _txSender,
                _blockTree,
                _stateReader, 
                _tracerFactory,
                _eciesCipher,
                _specProvider,
                _signer,
                _chainId);
        }
    }
}
