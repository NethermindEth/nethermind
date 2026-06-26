// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Linq;
using Nethermind.Abi;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.Consensus.AuRa.Validators
{
    public partial class ContractBasedValidator : AuRaValidatorBase, IDisposable
    {
        private readonly ILogger _logger;

        private PendingValidators? _currentPendingValidators;
        private ulong? _lastProcessedBlockNumber = null;
        private Hash256? _lastProcessedBlockHash = null;
        private IAuRaBlockFinalizationManager _blockFinalizationManager = null!;
        internal IBlockTree BlockTree { get; }
        private readonly IReceiptFinder _receiptFinder;

        internal IValidatorContract ValidatorContract { get; }

        public ContractBasedValidator(
            IValidatorContract validatorContract,
            IBlockTree blockTree,
            IReceiptFinder receiptFinder,
            IValidatorStore validatorStore,
            IValidSealerStrategy validSealerStrategy,
            IAuRaBlockFinalizationManager finalizationManager,
            BlockHeader? parentHeader,
            ILogManager logManager,
            ulong startBlockNumber,
            ulong posdaoTransition = ulong.MaxValue,
            bool forSealing = false) : base(validSealerStrategy, validatorStore, logManager, startBlockNumber, forSealing)
        {
            BlockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _receiptFinder = receiptFinder ?? throw new ArgumentNullException(nameof(receiptFinder));
            _posdaoTransition = posdaoTransition;
            _logger = logManager?.GetClassLogger<ContractBasedValidator>() ?? throw new ArgumentNullException(nameof(logManager));
            ValidatorContract = validatorContract ?? throw new ArgumentNullException(nameof(validatorContract));
            _currentPendingValidators = ValidatorStore.PendingValidators;
            SetFinalizationManager(finalizationManager, parentHeader ?? BlockTree.Head?.Header);
        }

        private void SetFinalizationManager(IAuRaBlockFinalizationManager finalizationManager, BlockHeader? parentHeader)
        {
            _blockFinalizationManager = finalizationManager ?? throw new ArgumentNullException(nameof(finalizationManager));

            if (!ForSealing)
            {
                _blockFinalizationManager.BlocksFinalized += OnBlocksFinalized;

                if (parentHeader is not null)
                {
                    Validators = LoadValidatorsFromContract(parentHeader);
                    InitValidatorStore();
                }
            }
        }

        public void Dispose() => _blockFinalizationManager.BlocksFinalized -= OnBlocksFinalized;

        public override void OnBlockProcessingStart(Block block, ProcessingOptions options = ProcessingOptions.None)
        {
            if (block.IsGenesis)
            {
                return;
            }

            bool isInitBlock = InitBlockNumber == block.Number;
            bool isProducingBlock = options.ContainsFlag(ProcessingOptions.ProducingBlock);
            bool isMainChainProcessing = !ForSealing && !isProducingBlock;
            bool isInProcessedRange = _lastProcessedBlockNumber is not null && block.Number - 1 <= _lastProcessedBlockNumber;
            bool isConsecutiveBlock = _lastProcessedBlockHash is not null && block.ParentHash == _lastProcessedBlockHash;

            // this condition is probably redundant because whenever Validators is null, isConsecutiveBlock will be false
            // but let's leave it here just in case, it does not harm
            Address[]? currentValidators = Validators;
            Address[] validators;
            if (currentValidators is null || (!isConsecutiveBlock && !isInitBlock))
            {
                BlockHeader? parentHeader = BlockTree.FindParentHeader(block.Header, BlockTreeLookupOptions.None);
                validators = isInitBlock || !isInProcessedRange ? LoadValidatorsFromContract(parentHeader) : ValidatorStore.GetValidators(block.Number);
                Validators = validators;

                if (isMainChainProcessing)
                {
                    if (_logger.IsInfo)
                        _logger.Info($"{(isInitBlock ? "Initial" : "Current")} contract validators ({validators.Length}): [{string.Join<Address>(", ", validators)}].");
                }
            }
            else
            {
                validators = currentValidators;
            }

            if (isInitBlock)
            {
                if (isMainChainProcessing)
                {
                    ValidatorStore.SetValidators(InitBlockNumber, validators);
                }
            }
            else
            {
                if (isMainChainProcessing && !isInProcessedRange)
                {
                    bool loadedValidatorsAreSameInStore = (ValidatorStore.GetValidators()?.SequenceEqual(validators) == true);
                    if (!loadedValidatorsAreSameInStore)
                    {
                        ValidatorStore.SetValidators(_blockFinalizationManager.GetLastLevelFinalizedBy(block.ParentHash!), validators);
                    }
                }

                if (isProducingBlock)
                {
                    // if we are producing blocks we are not on consecutive blocks.
                    // We need to initialize pending validators from db on each block being produced.
                    _currentPendingValidators = ValidatorStore.PendingValidators;
                }
                else if (!isConsecutiveBlock) // either reorg or blocks skipped (like fast sync)
                    _currentPendingValidators = ValidatorStore.PendingValidators = TryGetInitChangeFromPastBlocks(block.ParentHash!);
            }


            base.OnBlockProcessingStart(block, options);

            FinalizePendingValidatorsIfNeeded(block.Header, isProducingBlock);

            (_lastProcessedBlockNumber, _lastProcessedBlockHash) = (block.Number, block.Hash);
        }

        private PendingValidators? TryGetInitChangeFromPastBlocks(Hash256 blockHash)
        {
            PendingValidators? pendingValidators = null;
            ulong lastFinalized = _blockFinalizationManager.GetLastLevelFinalizedBy(blockHash);
            ulong toBlock = Math.Max(lastFinalized, InitBlockNumber);
            Block? block = BlockTree.FindBlock(blockHash, BlockTreeLookupOptions.None);
            while (block?.Number >= toBlock)
            {
                TxReceipt[] receipts = _receiptFinder.Get(block) ?? [];
                if (ValidatorContract.CheckInitiateChangeEvent(block.Header, receipts, out Address[]? potentialValidators))
                {
                    if (Validators is not null && Validators.SequenceEqual(potentialValidators))
                    {
                        break; // TODO: why this?
                    }

                    pendingValidators = new PendingValidators(block.Number, block.Hash!, potentialValidators);
                }
                block = BlockTree.FindBlock(block.ParentHash!, BlockTreeLookupOptions.None);
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

            if (ValidatorContract.CheckInitiateChangeEvent(block.Header, receipts, out Address[]? potentialValidators))
            {
                bool isProducingBlock = options.ContainsFlag(ProcessingOptions.ProducingBlock);

                // We are ignoring the signal if there are already pending validators.
                // This replicates openethereum's behaviour which can be seen as a bug.
                if (_currentPendingValidators is null && potentialValidators.Length > 0)
                {
                    _currentPendingValidators = new PendingValidators(block.Number, block.Hash!, potentialValidators);
                    if (!isProducingBlock)
                    {
                        ValidatorStore.PendingValidators = _currentPendingValidators;

                        if (_logger.IsInfo)
                            _logger.Info($"Signal for transition within contract at block {block.ToString(Block.Format.Short)}. New list of {potentialValidators.Length} : [{string.Join<Address>(", ", potentialValidators)}].");
                    }
                }
            }
        }

        private void FinalizePendingValidatorsIfNeeded(BlockHeader block, bool isProducingBlock)
        {
            ValidatorInfo validatorsInfo = ValidatorStore.GetValidatorsInfo(block.Number);
            bool isInitialValidatorSet = validatorsInfo.FinalizingBlockNumber == InitBlockNumber
                                        && (validatorsInfo.PreviousFinalizingBlockNumber == ulong.MaxValue || validatorsInfo.PreviousFinalizingBlockNumber < InitBlockNumber);

            if (InitBlockNumber == block.Number || (!isInitialValidatorSet && validatorsInfo.FinalizingBlockNumber == block.Number - 1))
            {
                if (_logger.IsInfo && !isProducingBlock)
                    _logger.Info($"Applying validator set change before block {block.ToString(BlockHeader.Format.Short)}.");

                if (block.Number == InitBlockNumber)
                    ValidatorContract.EnsureSystemAccount();

                ValidatorContract.FinalizeChange(block);
            }
        }

        private Address[] LoadValidatorsFromContract(BlockHeader? parentHeader)
        {
            try
            {
                Address[] validators = ValidatorContract.GetValidators(parentHeader);

                if (validators.Length == 0)
                {
                    throw new AuRaException("Failed to initialize validators list.");
                }

                return validators;
            }
            catch (AbiException e)
            {
                string parentHeaderDescription = parentHeader?.ToString(BlockHeader.Format.FullHashAndNumber) ?? "<missing parent header>";
                throw new AuRaException($"Failed to initialize validators list on block {parentHeaderDescription}\n{new StackTrace()}.", e);
            }
        }

        // NOTE: this is only added to `_blockFinalizationManager.BlocksFinalized` when `!ForSealing`
        private void OnBlocksFinalized(object? sender, AuRaFinalizeEventArgs e)
        {
            if (e.FinalizedBlocks.Any(header => header.Hash == _currentPendingValidators?.BlockHash))
            {
                Address[] validators = _currentPendingValidators!.Addresses;
                Validators = validators;
                ValidatorStore.SetValidators(e.FinalizingBlock.Number, validators);
                if (_logger.IsInfo)
                    _logger.Info($"Finalizing validators for transition signalled within contract at block {_currentPendingValidators.BlockNumber} after block {e.FinalizingBlock.ToString(BlockHeader.Format.Short)}.");
                _currentPendingValidators = ValidatorStore.PendingValidators = null;
            }
        }

        public override string ToString() => $"{nameof(ContractBasedValidator)}";

    }
}
