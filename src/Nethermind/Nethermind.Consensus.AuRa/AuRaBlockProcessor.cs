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
            ITxPermissionFilter txFilter = null)
            : base(specProvider, blockValidator, rewardCalculator, transactionProcessor, stateDb, codeDb, stateProvider, storageProvider, txPool, receiptStorage, logManager)
        {
            _auRaBlockProcessorExtension = auRaBlockProcessorExtension ?? throw new ArgumentNullException(nameof(auRaBlockProcessorExtension));
            _logger = logManager?.GetClassLogger<AuRaBlockProcessor>() ?? throw new ArgumentNullException(nameof(logManager));
            _txFilter = txFilter;
        }

        protected override TxReceipt[] ProcessBlock(Block block, IBlockTracer blockTracer, ProcessingOptions options)
        {
            ValidateTxs(block);
            _auRaBlockProcessorExtension.PreProcess(block, options);
            var receipts = base.ProcessBlock(block, blockTracer, options);
            _auRaBlockProcessorExtension.PostProcess(block, receipts, options);
            return receipts;
        }

        private void ValidateTxs(Block block)
        {
            if (_txFilter != null)
            {
                for (int i = 0; i < block.Transactions.Length; i++)
                {
                    var tx = block.Transactions[i];
                    if (!_txFilter.IsAllowed(tx, block.Header))
                    {
                        if (_logger.IsError) _logger.Error($"Proposed block is not valid {block.ToString(Block.Format.FullHashAndNumber)}. {tx.ToShortString()} doesn't have required permissions.");
                        throw new InvalidBlockException(block.Hash);
                    }
                }
            }
        }
    }
}