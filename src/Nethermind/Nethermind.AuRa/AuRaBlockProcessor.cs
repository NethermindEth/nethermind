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
using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Rewards;
using Nethermind.Blockchain.Validators;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.Store;
using Nethermind.TxPool;

namespace Nethermind.AuRa
{
    public class AuRaBlockProcessor : BlockProcessor
    {
        private readonly IAuRaBlockProcessorExtension _auRaBlockProcessorExtension;

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
            IAuRaBlockProcessorExtension auRaBlockProcessorExtension)
            : base(specProvider, blockValidator, rewardCalculator, transactionProcessor, stateDb, codeDb, stateProvider, storageProvider, txPool, receiptStorage, logManager)
        {
            _auRaBlockProcessorExtension = auRaBlockProcessorExtension ?? throw new ArgumentNullException(nameof(auRaBlockProcessorExtension));
        }

        protected override TxReceipt[] ProcessBlock(Block block, IBlockTracer blockTracer, ProcessingOptions options)
        {
            _auRaBlockProcessorExtension.PreProcess(block, options);
            var receipts = base.ProcessBlock(block, blockTracer, options);
            _auRaBlockProcessorExtension.PostProcess(block, receipts, options);
            return receipts;
        }
    }
}