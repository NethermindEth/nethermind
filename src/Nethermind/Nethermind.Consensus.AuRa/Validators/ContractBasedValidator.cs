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

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Nethermind.Abi;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State;
using Nethermind.Db.Blooms;

namespace Nethermind.Consensus.AuRa.Validators
{
    public partial class ContractBasedValidator : AuRaValidatorBase, IDisposable
    {
        private readonly ILogger _logger;
       
        private PendingValidators _currentPendingValidators;
        private long _lastProcessedBlockNumber = 0;
        private IAuRaBlockFinalizationManager _blockFinalizationManager;
        internal IBlockTree BlockTree { get; }
        private readonly IReceiptFinder _receiptFinder;
        
        internal IValidatorContract ValidatorContract { get; }
        private PendingValidators CurrentPendingValidators => _currentPendingValidators;

        public ContractBasedValidator(
            IValidatorContract validatorContract,
            IBlockTree blockTree,
            IReceiptFinder receiptFinder,
            IValidatorStore validatorStore,
            IValidSealerStrategy validSealerStrategy,
            IAuRaBlockFinalizationManager finalizationManager, 
            BlockHeader parentHeader,
            ILogManager logManager,
            long startBlockNumber,
            long posdaoTransition = long.MaxValue,
            bool forSealing = false) : base(validSealerStrategy, validatorStore, logManager, startBlockNumber, forSealing)
        {
            BlockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _receiptFinder = receiptFinder ?? throw new ArgumentNullException(nameof(receiptFinder));
            _posdaoTransition = posdaoTransition;
            _logger = logManager?.GetClassLogger<ContractBasedValidator>() ?? throw new ArgumentNullException(nameof(logManager));
            ValidatorContract = validatorContract ?? throw new ArgumentNullException(nameof(validatorContract));
            SetPendingValidators(LoadPendingValidators());
            SetFinalizationManager(finalizationManager, parentHeader ?? BlockTree.Head?.Header);
        }

        private void SetFinalizationManager(IAuRaBlockFinalizationManager finalizationManager, BlockHeader parentHeader)
        {
            _blockFinalizationManager = finalizationManager ?? throw new ArgumentNullException(nameof(finalizationManager));

            if (!ForSealing)
            {
                _blockFinalizationManager.BlocksFinalized += OnBlocksFinalized;
                
                if (parentHeader != null)
                {
                    Validators = LoadValidatorsFromContract(parentHeader);
                    InitValidatorStore();
                }
            }
        }

        public void Dispose()
        {
            _blockFinalizationManager.BlocksFinalized -= OnBlocksFinalized;
        }

        public override void OnBlockProcessingStart(Block block, ProcessingOptions options = ProcessingOptions.None)
        {
            if (block.IsGenesis)
            {
                return;
            }
            
            var isProducingBlock = options.IsProducingBlock();
            var isProcessingBlock = !isProducingBlock;
            var isInitBlock = InitBlockNumber == block.Number;
            var notConsecutiveBlock = block.Number - 1 > _lastProcessedBlockNumber || _lastProcessedBlockNumber == 0;
            var shouldLoadValidators = Validators == null || notConsecutiveBlock || isProducingBlock;
            var mainChainProcessing = !ForSealing && isProcessingBlock;
            
            if (shouldLoadValidators)
            {
                Validators = isInitBlock || notConsecutiveBlock
                    ? LoadValidatorsFromContract(BlockTree.FindParentHeader(block.Header, BlockTreeLookupOptions.None)) 
                    : ValidatorStore.GetValidators();

                if (mainChainProcessing)
                {
                    if (_logger.IsInfo) _logger.Info($"{(isInitBlock ? "Initial" : "Current")} contract validators ({Validators.Length}): [{string.Join<Address>(", ", Validators)}].");
                }
            }
            
            if (isInitBlock)
            {
                if (mainChainProcessing)
                {
                    ValidatorStore.SetValidators(InitBlockNumber, Validators);
                }
                
                InitiateChange(block, Validators.ToArray(), isProcessingBlock, true);
            }
            else
            {
                if (mainChainProcessing && notConsecutiveBlock)
                {
                    bool loadedValidatorsAreSameInStore = (ValidatorStore.GetValidators()?.SequenceEqual(Validators) == true);
                    if (!loadedValidatorsAreSameInStore)
                    {
                        ValidatorStore.SetValidators(_blockFinalizationManager.GetLastLevelFinalizedBy(block.ParentHash), Validators);
                    }
                }
                
                if (isProcessingBlock)
                {
                    bool reorganisationHappened = block.Number <= _lastProcessedBlockNumber;
                    if (reorganisationHappened)
                    {
                        var reorganisationToBlockBeforePendingValidatorsInitChange = block.Number <= CurrentPendingValidators?.BlockNumber;
                        SetPendingValidators(reorganisationToBlockBeforePendingValidatorsInitChange ? null : LoadPendingValidators(), reorganisationToBlockBeforePendingValidatorsInitChange);

                    }
                    else if (block.Number > _lastProcessedBlockNumber + 1) // blocks skipped, like fast sync
                    {
                        SetPendingValidators(TryGetInitChangeFromPastBlocks(block.ParentHash), true);
                    }
                }
                else
                {
                    // if we are not processing blocks we are not on consecutive blocks.
                    // We need to initialize pending validators from db on each block being produced.  
                    SetPendingValidators(LoadPendingValidators());
                }
            }
            
            base.OnBlockProcessingStart(block, options);
            
            FinalizePendingValidatorsIfNeeded(block.Header, isProcessingBlock);
            
            _lastProcessedBlockNumber = block.Number;
        }

        private PendingValidators TryGetInitChangeFromPastBlocks(Keccak blockHash)
        {
            PendingValidators pendingValidators = null;
            var lastFinalized = _blockFinalizationManager.GetLastLevelFinalizedBy(blockHash);
            var toBlock = Math.Max(lastFinalized, InitBlockNumber);
            var block = BlockTree.FindBlock(blockHash, BlockTreeLookupOptions.None);
            while (block?.Number >= toBlock)
            {
                var receipts = _receiptFinder.Get(block) ?? Array.Empty<TxReceipt>();
                if (ValidatorContract.CheckInitiateChangeEvent(block.Header, receipts, out var potentialValidators))
                {
                    if (Validators.SequenceEqual(potentialValidators))
                    {
                        break;
                    }

                    pendingValidators = new PendingValidators(block.Number, block.Hash, potentialValidators);
                }
                block = BlockTree.FindBlock(block.ParentHash, BlockTreeLookupOptions.None);
            }

            return pendingValidators;
        }

        public override void OnBlockProcessingEnd(Block block, TxReceipt[] receipts, ProcessingOptions options = ProcessingOptions.None)
        {
            base.OnBlockProcessingEnd(block, receipts, options);
            
            if (block.IsGenesis)
            {
                ValidatorStore.SetValidators(block.Number, LoadValidatorsFromContract(block.Header));
            }
            
            if (ValidatorContract.CheckInitiateChangeEvent(block.Header, receipts, out var potentialValidators))
            {
                var isProcessingBlock = !options.IsProducingBlock();
                InitiateChange(block, potentialValidators, isProcessingBlock, Validators.Length == 1);
                if (_logger.IsInfo && isProcessingBlock) _logger.Info($"Signal for transition within contract at block {block.ToString(Block.Format.Short)}. New list of {potentialValidators.Length} : [{string.Join<Address>(", ", potentialValidators)}].");
            }
        }

        private void FinalizePendingValidatorsIfNeeded(BlockHeader block, bool isProcessingBlock)
        {
            if (CurrentPendingValidators?.AreFinalized == true)
            {
                if (_logger.IsInfo && isProcessingBlock) _logger.Info($"Applying validator set change signalled at block {CurrentPendingValidators.BlockNumber} before block {block.ToString(BlockHeader.Format.Short)}.");
                if (block.Number == InitBlockNumber)
                {
                    ValidatorContract.EnsureSystemAccount();
                    ValidatorContract.FinalizeChange(block);
                }
                else
                {
                    ValidatorContract.FinalizeChange(block);
                }
                SetPendingValidators(null, isProcessingBlock);
            }
        }
        
        private void InitiateChange(Block block, Address[] potentialValidators, bool isProcessingBlock, bool initiateChangeIsImmediatelyFinalized = false)
        {
            // We are ignoring the signal if there are already pending validators. This replicates Parity behaviour which can be seen as a bug.
            if (CurrentPendingValidators == null && potentialValidators.Length > 0)
            {
                SetPendingValidators(new PendingValidators(block.Number, block.Hash, potentialValidators)
                    {
                        AreFinalized = initiateChangeIsImmediatelyFinalized
                    },
                    !initiateChangeIsImmediatelyFinalized && isProcessingBlock);
            }
        }

        private Address[] LoadValidatorsFromContract(BlockHeader parentHeader)
        {
            try
            {
                var validators = ValidatorContract.GetValidators(parentHeader);
                
                if (validators.Length == 0)
                {
                    throw new AuRaException("Failed to initialize validators list.");
                }

                return validators;
            }
            catch (AbiException e)
            {
                throw new AuRaException($"Failed to initialize validators list on block {parentHeader.ToString(BlockHeader.Format.FullHashAndNumber)} {new StackTrace()}.", e);
            }
        }

        private void OnBlocksFinalized(object sender, FinalizeEventArgs e)
        {
            if (CurrentPendingValidators != null)
            {
                // .Any equivalent with for
                var currentPendingValidatorsBlockGotFinalized = false;
                for (int i = 0; i < e.FinalizedBlocks.Count && !currentPendingValidatorsBlockGotFinalized; i++)
                {
                    currentPendingValidatorsBlockGotFinalized = e.FinalizedBlocks[i].Hash == CurrentPendingValidators.BlockHash;
                }
                
                if (currentPendingValidatorsBlockGotFinalized)
                {
                    CurrentPendingValidators.AreFinalized = true;
                    Validators = CurrentPendingValidators.Addresses;
                    SetPendingValidators(CurrentPendingValidators, true);
                    if (!ForSealing)
                    {
                        ValidatorStore.SetValidators(e.FinalizingBlock.Number, Validators);
                        if (_logger.IsInfo) _logger.Info($"Finalizing validators for transition within contract signalled at block {CurrentPendingValidators.BlockNumber}. after block {e.FinalizingBlock.ToString(BlockHeader.Format.Short)}.");
                    }
                }
            }
        }

        private PendingValidators LoadPendingValidators() => ValidatorStore.PendingValidators;

        private void SetPendingValidators(PendingValidators validators, bool canSave = false)
        {
            _currentPendingValidators = validators;
            
            // We don't want to save to db when:
            // * We are producing block
            // * We will save later on processing same block (stateDb ignores consecutive calls with same key!)
            // * We are loading validators from db.
            if (canSave)
            {
                ValidatorStore.PendingValidators = validators;
            }
        }
        
        public override string ToString() => $"{nameof(ContractBasedValidator)}";

    }
}
