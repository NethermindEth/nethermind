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

using System;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Rewards;
using Nethermind.Blockchain.Validators;
using Nethermind.Consensus.AuRa.Transactions;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Store;
using Nethermind.TxPool;

namespace Nethermind.Consensus.AuRa
{
    public class AuRaBlockProcessor : BlockProcessor
    {
        private readonly IAuRaBlockProcessorExtension _auRaBlockProcessorExtension;
        private readonly IBlockTree _blockTree;
        private readonly IGasLimitOverride _gasLimitOverride;
        private readonly ITxPermissionFilter _txFilter;
        private readonly ILogger _logger;

        public AuRaBlockProcessor(
            ISpecProvider specProvider,
            IBlockValidator blockValidator,
            IRewardCalculator rewardCalculator,
            ITransactionProcessor transactionProcessor,
            ISnapshotableDb stateDb,
            ISnapshotableDb codeDb,
            IStateProvider stateProvider,
            IStorageProvider storageProvider,
            ITxPool txPool,
            IReceiptStorage receiptStorage,
            ILogManager logManager,
            IAuRaBlockProcessorExtension auRaBlockProcessorExtension,
            IBlockTree blockTree,
            ITxPermissionFilter txFilter = null,
            IGasLimitOverride gasLimitOverride = null)
            : base(specProvider, blockValidator, rewardCalculator, transactionProcessor, stateDb, codeDb, stateProvider, storageProvider, txPool, receiptStorage, logManager)
        {
            _auRaBlockProcessorExtension = auRaBlockProcessorExtension ?? throw new ArgumentNullException(nameof(auRaBlockProcessorExtension));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _logger = logManager?.GetClassLogger<AuRaBlockProcessor>() ?? throw new ArgumentNullException(nameof(logManager));
            _txFilter = txFilter ?? NullTxPermissionFilter.Instance;
            _gasLimitOverride = gasLimitOverride;
        }

        protected override TxReceipt[] ProcessBlock(Block block, IBlockTracer blockTracer, ProcessingOptions options)
        {
            ValidateAuRa(block);
            _auRaBlockProcessorExtension.PreProcess(block, options);
            var receipts = base.ProcessBlock(block, blockTracer, options);
            _auRaBlockProcessorExtension.PostProcess(block, receipts, options);
            return receipts;
        }

        // This validations cannot be run in AuraSealValidator because they are dependent on state.
        private void ValidateAuRa(Block block)
        {
            if (!block.IsGenesis)
            {
                var parentHeader = _blockTree.FindParentHeader(block.Header, BlockTreeLookupOptions.None);
                
                ValidateGasLimit(block.Header, parentHeader);
                ValidateTxs(block, parentHeader);
            }
        }

        private void ValidateGasLimit(BlockHeader header, BlockHeader parentHeader)
        {
            var gasLimit = _gasLimitOverride?.GetGasLimit(parentHeader);
            if (gasLimit.HasValue && header.GasLimit != gasLimit)
            {
                if (_logger.IsError) _logger.Error($"Invalid gas limit for block {header.Number}, hash {header.Hash}, expected value from contract {gasLimit.Value}, but found {header.GasLimit}.");
                throw new InvalidBlockException(header.Hash);
            }
        }

        private void ValidateTxs(Block block, BlockHeader parentHeader)
        {
            for (int i = 0; i < block.Transactions.Length; i++)
            {
                var tx = block.Transactions[i];
                if (!_txFilter.IsAllowed(tx, parentHeader))
                {
                    if (_logger.IsError) _logger.Error($"Proposed block is not valid {block.ToString(Block.Format.FullHashAndNumber)}. {tx.ToShortString()} doesn't have required permissions.");
                    throw new InvalidBlockException(block.Hash);
                }
            }
        }
    }
}