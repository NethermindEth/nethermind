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

using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Rewards;
using Nethermind.Blockchain.Validators;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Mev
{
    public class MevBlockProcessor : BlockProcessor, IBeneficiaryBalanceSource
    {
        private readonly IStateProvider _stateProvider;

        public MevBlockProcessor(
            ISpecProvider? specProvider, 
            IBlockValidator? blockValidator, 
            IRewardCalculator? rewardCalculator,
            ITransactionProcessor? transactionProcessor, 
            IStateProvider? stateProvider, 
            IStorageProvider? storageProvider,
            IReceiptStorage? receiptStorage, 
            IWitnessCollector? witnessCollector, 
            ILogManager? logManager) 
            : base(specProvider, blockValidator, rewardCalculator, transactionProcessor, stateProvider, storageProvider, receiptStorage, witnessCollector, logManager)
        {
            _stateProvider = stateProvider!;
        }

        protected override TxReceipt[] ProcessBlock(Block block, IBlockTracer blockTracer, ProcessingOptions options)
        {
            TxReceipt[] processBlock = base.ProcessBlock(block, blockTracer, options);
            BeneficiaryBalance = _stateProvider.GetBalance(block.Header.GasBeneficiary!);
            return processBlock;
        }

        public UInt256 BeneficiaryBalance { get; private set; }
    }
}
