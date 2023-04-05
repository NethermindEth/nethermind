// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus;
using Nethermind.Core;
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
        private readonly IJsonRpcConfig _jsonRpcConfig;
        private readonly IBundlePool _bundlePool;
        private readonly IBlockTree _blockTree;
        private readonly IStateReader _stateReader;
        private readonly ITracerFactory _tracerFactory;
        private readonly ISpecProvider _specProvider;
        private readonly ISigner? _signer;
        private readonly BlockValidationService _blockValidationService;

        public MevModuleFactory(
            IJsonRpcConfig jsonRpcConfig,
            IBundlePool bundlePool,
            IBlockTree blockTree,
            IStateReader stateReader,
            ITracerFactory tracerFactory,
            ISpecProvider specProvider,
            ISigner? signer,
            BlockValidationService blockValidationService)
        {
            _jsonRpcConfig = jsonRpcConfig;
            _bundlePool = bundlePool;
            _blockTree = blockTree;
            _stateReader = stateReader;
            _tracerFactory = tracerFactory;
            _specProvider = specProvider;
            _signer = signer;
            _blockValidationService = blockValidationService;
        }

        public override IMevRpcModule Create()
        {
            return new MevRpcModule(
                _jsonRpcConfig,
                _bundlePool,
                _blockTree,
                _stateReader,
                _tracerFactory,
                _specProvider,
                _signer,
                _blockValidationService);
        }
    }
}
