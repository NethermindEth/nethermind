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

using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Rewards;
using Nethermind.Blockchain.TxPools;
using Nethermind.Blockchain.Validators;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Logging;
using Nethermind.Store;

namespace Nethermind.AuRa.Validators
{
    public abstract class AuRaValidatorProcessor : BlockProcessor, IAuRaValidator
    {
        protected virtual Address[] Validators { get; set; }
        public virtual int MinSealersForFinalization => Validators.MinSealersForFinalization();
        public virtual int CurrentSealersCount => Validators.Length;

        public virtual bool IsValidSealer(Address address, long step) => Validators.GetItemRoundRobin(step) == address;

        public virtual void SetFinalizationManager(IBlockFinalizationManager finalizationManager, bool forProducing)
        {
            SetFinalizationManagerInternal(finalizationManager, forProducing);
        }

        protected virtual void SetFinalizationManagerInternal(IBlockFinalizationManager finalizationManager, in bool forSealing) { }

        protected override void PreProcess(Block block, ProcessingOptions options)
        {
            if (!options.IsProducingBlock())
            {
                var auRaStep = block.Header.AuRaStep.Value;
                if (!IsValidSealer(block.Beneficiary, auRaStep))
                {
                    if (_logger.IsError) _logger.Error($"Block from incorrect proposer at block {block.ToString(Block.Format.FullHashAndNumber)}, step {auRaStep} from author {block.Beneficiary}.");
                    throw new InvalidBlockException(block.Hash);
                }
            }
        }

        protected override void PostProcess(Block block, TxReceipt[] receipts, ProcessingOptions options) { }

        protected AuRaValidatorProcessor(ISpecProvider specProvider, IBlockValidator blockValidator, IRewardCalculator rewardCalculator, ITransactionProcessor transactionProcessor, ISnapshotableDb stateDb, ISnapshotableDb codeDb, IDb traceDb, IStateProvider stateProvider, IStorageProvider storageProvider, ITxPool txPool, IReceiptStorage receiptStorage, ILogManager logManager) : base(specProvider, blockValidator, rewardCalculator, transactionProcessor, stateDb, codeDb, traceDb, stateProvider, storageProvider, txPool, receiptStorage, logManager)
        {
        }
    }
}