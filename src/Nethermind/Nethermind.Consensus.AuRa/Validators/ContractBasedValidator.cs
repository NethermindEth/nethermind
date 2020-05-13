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
using System.Linq;
using Nethermind.Abi;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State;
using Nethermind.Store;

namespace Nethermind.Consensus.AuRa.Validators
{
    public class ContractBasedValidator : AuRaValidatorProcessorExtension
    {
        private readonly ILogger _logger;
        private readonly IStateProvider _stateProvider;
        private readonly ITransactionProcessor _transactionProcessor;
        private readonly IReadOnlyTransactionProcessorSource _readOnlyReadOnlyTransactionProcessorSource;

        private ValidatorContract _validatorContract;
        private PendingValidators _currentPendingValidators;
        private long _lastProcessedBlockNumber = 0;
        private IBlockFinalizationManager _blockFinalizationManager;
        private readonly IBlockTree _blockTree;
        private readonly IReceiptFinder _receiptFinder;
        private readonly IValidatorStore _validatorStore;
        private bool _validatorUsedForSealing;

        protected Address ContractAddress { get; }
        protected IAbiEncoder AbiEncoder { get; }
        protected long InitBlockNumber { get; }
        protected ValidatorContract ValidatorContract => _validatorContract ??= CreateValidatorContract(ContractAddress);

        private PendingValidators CurrentPendingValidators => _currentPendingValidators;

        public ContractBasedValidator(
            AuRaParameters.Validator validator,
            IStateProvider stateProvider,
            IAbiEncoder abiEncoder,
            ITransactionProcessor transactionProcessor,
            IReadOnlyTransactionProcessorSource readOnlyTransactionProcessorSource,
            IBlockTree blockTree,
            IReceiptFinder receiptFinder,
            IValidatorStore validatorStore,
            IValidSealerStrategy validSealerStrategy,
            ILogManager logManager,
            long startBlockNumber) : base(validator, validSealerStrategy, logManager)
        {
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            ContractAddress = validator.Addresses?.FirstOrDefault() ?? throw new ArgumentException("Missing contract address for AuRa validator.", nameof(validator.Addresses));
            _stateProvider = stateProvider ?? throw new ArgumentNullException(nameof(stateProvider));
            _transactionProcessor = transactionProcessor ?? throw new ArgumentNullException(nameof(transactionProcessor));
            _readOnlyReadOnlyTransactionProcessorSource = readOnlyTransactionProcessorSource ?? throw new ArgumentNullException(nameof(readOnlyTransactionProcessorSource));
            _receiptFinder = receiptFinder ?? throw new ArgumentNullException(nameof(receiptFinder));
            _validatorStore = validatorStore ?? throw new ArgumentNullException(nameof(validatorStore));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            AbiEncoder = abiEncoder ?? throw new ArgumentNullException(nameof(abiEncoder));
            InitBlockNumber = startBlockNumber;
            SetPendingValidators(LoadPendingValidators());
        }

        public override void SetFinalizationManager(IBlockFinalizationManager finalizationManager, in bool forSealing)
        {
            base.SetFinalizationManager(finalizationManager, in forSealing);
            
            if (_blockFinalizationManager != null)
            {
                _blockFinalizationManager.BlocksFinalized -= OnBlocksFinalized;
            }

            _blockFinalizationManager = finalizationManager;
            _validatorUsedForSealing = forSealing;
            
            if (!forSealing && _blockFinalizationManager != null)
            {
                _blockFinalizationManager.BlocksFinalized += OnBlocksFinalized;
                if (_blockTree.Head != null)
                {
                    Validators = LoadValidatorsFromContract(_blockTree.Head?.Header);
                }
            }
        }

        public override void PreProcess(Block block, ProcessingOptions options = ProcessingOptions.None)
        {
            if (block.IsGenesis)
            {
                _validatorStore.SetValidators(block.Number, LoadValidatorsFromContract(block.Header));
                return;
            }
            
            var isProducingBlock = options.IsProducingBlock();
            var isProcessingBlock = !isProducingBlock;
            var isInitBlock = InitBlockNumber == block.Number;
            var shouldLoadValidators = Validators == null || isProducingBlock;
            var mainChainProcessing = !_validatorUsedForSealing && isProcessingBlock;
            
            if (shouldLoadValidators)
            {
                Validators = isInitBlock 
                    ? LoadValidatorsFromContract(_blockTree.FindParentHeader(block.Header, BlockTreeLookupOptions.None)) 
                    : _validatorStore.GetValidators();

                if (mainChainProcessing)
                {
                    if (_logger.IsInfo) _logger.Info($"{(isInitBlock ? "Initial" : "Current")} contract validators ({Validators.Length}): [{string.Join<Address>(", ", Validators)}].");
                }
            }
            
            if (isInitBlock)
            {
                if (mainChainProcessing)
                {
                    _validatorStore.SetValidators(InitBlockNumber, Validators);
                }
                
                InitiateChange(block, Validators.ToArray(), isProcessingBlock, true);
            }
            else
            {
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
            
            base.PreProcess(block, options);
            
            FinalizePendingValidatorsIfNeeded(block.Header, isProcessingBlock);
            
            _lastProcessedBlockNumber = block.Number;
        }

        private PendingValidators TryGetInitChangeFromPastBlocks(Keccak blockHash)
        {
            PendingValidators pendingValidators = null;
            var lastFinalized = _blockFinalizationManager.GetLastLevelFinalizedBy(blockHash);
            var toBlock = Math.Max(lastFinalized, InitBlockNumber);
            var block = _blockTree.FindBlock(blockHash, BlockTreeLookupOptions.None);
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
                block = _blockTree.FindBlock(block.ParentHash, BlockTreeLookupOptions.None);
            }

            return pendingValidators;
        }

        public override void PostProcess(Block block, TxReceipt[] receipts, ProcessingOptions options = ProcessingOptions.None)
        {
            base.PostProcess(block, receipts, options);
            
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
        
        protected virtual ValidatorContract CreateValidatorContract(Address contractAddress) => 
            new ValidatorContract(_transactionProcessor, AbiEncoder, contractAddress, _stateProvider, _readOnlyReadOnlyTransactionProcessorSource);
        
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
            var validators = ValidatorContract.GetValidators(parentHeader);

            if (validators.Length == 0)
            {
                throw new AuRaException("Failed to initialize validators list.");
            }

            return validators;
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
                    if (!_validatorUsedForSealing)
                    {
                        _validatorStore.SetValidators(e.FinalizingBlock.Number, Validators);
                        if (_logger.IsInfo) _logger.Info($"Finalizing validators for transition within contract signalled at block {CurrentPendingValidators.BlockNumber}. after block {e.FinalizingBlock.ToString(BlockHeader.Format.Short)}.");
                    }
                }
            }
        }

        private PendingValidators LoadPendingValidators() => _validatorStore.PendingValidators;

        private void SetPendingValidators(PendingValidators validators, bool canSave = false)
        {
            _currentPendingValidators = validators;
            
            // We don't want to save to db when:
            // * We are producing block
            // * We will save later on processing same block (stateDb ignores consecutive calls with same key!)
            // * We are loading validators from db.
            if (canSave)
            {
                _validatorStore.PendingValidators = validators;
            }
        }
    }
}