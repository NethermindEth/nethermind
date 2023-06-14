// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;

using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Tracing;
using Nethermind.Consensus.Validators;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;

namespace Nethermind.JsonRpc.Modules.Proof
{
    public class ProofModuleFactory : ModuleFactoryBase<IProofRpcModule>
    {
        private readonly IBlockPreprocessorStep _recoveryStep;
        private readonly IReceiptFinder _receiptFinder;
        private readonly ISpecProvider _specProvider;
        private readonly ILogManager _logManager;
        private readonly IReadOnlyBlockTree _blockTree;
        private readonly ReadOnlyDbProvider _dbProvider;
        private readonly IReadOnlyTrieStore _trieStore;

        public ProofModuleFactory(
            IDbProvider dbProvider,
            IBlockTree blockTree,
            IReadOnlyTrieStore trieStore,
            IBlockPreprocessorStep recoveryStep,
            IReceiptFinder receiptFinder,
            ISpecProvider specProvider,
            ILogManager logManager)
        {
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _recoveryStep = recoveryStep ?? throw new ArgumentNullException(nameof(recoveryStep));
            _receiptFinder = receiptFinder ?? throw new ArgumentNullException(nameof(receiptFinder));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _dbProvider = dbProvider.AsReadOnly(false);
            _blockTree = blockTree.AsReadOnly();
            _trieStore = trieStore;
        }

        public override IProofRpcModule Create()
        {
            ReadOnlyTxProcessingEnv txProcessingEnv = new(
                _dbProvider, _trieStore, _blockTree, _specProvider, _logManager);

            ReadOnlyChainProcessingEnv chainProcessingEnv = new(
                txProcessingEnv, Always.Valid, _recoveryStep, NoBlockRewards.Instance, new InMemoryReceiptStorage(), _dbProvider, _specProvider, _logManager);

            Tracer tracer = new(
                txProcessingEnv.StateProvider,
                chainProcessingEnv.ChainProcessor,
                chainProcessingEnv.ChainProcessor);

            return new ProofRpcModule(tracer, _blockTree, _receiptFinder, _specProvider, _logManager);
        }
    }
}
