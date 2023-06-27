// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Consensus.AuRa.Validators
{
    public class MultiValidator : IAuRaValidator, IReportingValidator, ITxSource, IDisposable
    {
        private readonly IAuRaValidatorFactory _validatorFactory;
        private readonly IBlockTree _blockTree;
        private readonly IValidatorStore _validatorStore;
        private readonly bool _forSealing;
        private IAuRaBlockFinalizationManager _blockFinalizationManager;
        private readonly IDictionary<long, AuRaParameters.Validator> _validators;
        private readonly ILogger _logger;
        private IAuRaValidator _currentValidator;
        private AuRaParameters.Validator _currentValidatorPrototype;
        private long _lastProcessedBlock = 0;

        public MultiValidator(
            AuRaParameters.Validator validator,
            IAuRaValidatorFactory validatorFactory,
            IBlockTree blockTree,
            IValidatorStore validatorStore,
            IAuRaBlockFinalizationManager finalizationManager,
            BlockHeader parentHeader,
            ILogManager logManager,
            bool forSealing = false)
        {
            if (validator is null) throw new ArgumentNullException(nameof(validator));
            if (validator.ValidatorType != AuRaParameters.ValidatorType.Multi) throw new ArgumentException("Wrong validator type.", nameof(validator));
            _validatorFactory = validatorFactory ?? throw new ArgumentNullException(nameof(validatorFactory));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _validatorStore = validatorStore ?? throw new ArgumentNullException(nameof(validatorStore));
            _forSealing = forSealing;
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));

            _validators = validator.Validators?.Count > 0
                ? validator.Validators
                : throw new ArgumentException("Multi validator cannot be empty.", nameof(validator.Validators));

            SetFinalizationManager(finalizationManager, parentHeader);
        }

        public Address[] Validators => _currentValidator?.Validators;

        private void InitCurrentValidator(long blockNumber, BlockHeader parentHeader)
        {
            if (TryGetLastValidator(blockNumber, out var validatorInfo))
            {
                SetCurrentValidator(validatorInfo, parentHeader);
            }

            _lastProcessedBlock = blockNumber;
        }

        private bool TryGetLastValidator(long blockNum, out KeyValuePair<long, AuRaParameters.Validator> validator)
        {
            var headNumber = _blockTree.Head?.Number ?? 0;

            validator = default;
            bool found = false;

            foreach (var kvp in _validators)
            {
                if (kvp.Key <= blockNum || kvp.Key <= headNumber && kvp.Value.ValidatorType.CanChangeImmediately())
                {
                    validator = kvp;
                    found = true;
                }
            }

            return found;
        }

        private void OnBlocksFinalized(object sender, FinalizeEventArgs e)
        {
            for (int i = 0; i < e.FinalizedBlocks.Count; i++)
            {
                var finalizedBlockHeader = e.FinalizedBlocks[i];
                if (TryGetValidator(finalizedBlockHeader.Number, out var validator) && !validator.ValidatorType.CanChangeImmediately())
                {
                    SetCurrentValidator(e.FinalizingBlock.Number, validator, e.FinalizingBlock);
                    if (!_forSealing)
                    {
                        if (_logger.IsInfo) _logger.Info($"Applying chainspec validator change signalled at block {finalizedBlockHeader.ToString(BlockHeader.Format.Short)} at block {e.FinalizingBlock.ToString(BlockHeader.Format.Short)}.");
                    }
                }
            }
        }

        public void OnBlockProcessingStart(Block block, ProcessingOptions options = ProcessingOptions.None)
        {
            if (!block.IsGenesis)
            {
                bool ValidatorWasAlreadyFinalized(KeyValuePair<long, AuRaParameters.Validator> validatorInfo) => _blockFinalizationManager.LastFinalizedBlockLevel >= validatorInfo.Key;

                bool isProducingBlock = options.ContainsFlag(ProcessingOptions.ProducingBlock);
                long previousBlockNumber = block.Number - 1;
                bool isNotConsecutive = previousBlockNumber != _lastProcessedBlock;

                if (isProducingBlock || isNotConsecutive)
                {
                    if (TryGetLastValidator(previousBlockNumber, out var validatorInfo))
                    {
                        var parentHeader = _blockTree.FindParentHeader(block.Header, BlockTreeLookupOptions.None);
                        if (validatorInfo.Value.ValidatorType.CanChangeImmediately() || ValidatorWasAlreadyFinalized(validatorInfo))
                        {
                            SetCurrentValidator(validatorInfo, parentHeader);
                        }
                        else if (!isProducingBlock)
                        {
                            bool canSetValidatorAsCurrent = !TryGetLastValidator(validatorInfo.Key - 1, out var previousValidatorInfo);
                            long? finalizedAtBlockNumber = null;
                            if (!canSetValidatorAsCurrent)
                            {
                                SetCurrentValidator(previousValidatorInfo, parentHeader);
                                finalizedAtBlockNumber = _blockFinalizationManager.GetFinalizationLevel(validatorInfo.Key);
                                canSetValidatorAsCurrent = finalizedAtBlockNumber is not null;
                            }

                            if (canSetValidatorAsCurrent)
                            {
                                SetCurrentValidator(finalizedAtBlockNumber ?? validatorInfo.Key, validatorInfo.Value, parentHeader);
                            }
                        }
                    }
                }
            }

            _currentValidator?.OnBlockProcessingStart(block, options);
        }

        private bool TryGetValidator(long blockNumber, out AuRaParameters.Validator validator) => _validators.TryGetValue(blockNumber, out validator);

        public void OnBlockProcessingEnd(Block block, TxReceipt[] receipts, ProcessingOptions options = ProcessingOptions.None)
        {
            _currentValidator?.OnBlockProcessingEnd(block, receipts, options);

            if (!block.IsGenesis)
            {
                bool notProducing = !options.ContainsFlag(ProcessingOptions.ProducingBlock);

                if (TryGetValidator(block.Number, out var validator))
                {
                    if (validator.ValidatorType.CanChangeImmediately())
                    {
                        SetCurrentValidator(block.Number, validator, _blockTree.FindParentHeader(block.Header, BlockTreeLookupOptions.None));
                        if (_logger.IsInfo && notProducing) _logger.Info($"Immediately applying chainspec validator change signalled at block {block.ToString(Block.Format.Short)} to {validator.ValidatorType}.");
                    }
                    else if (_logger.IsInfo && notProducing) _logger.Info($"Signal for switch to chainspec {validator.ValidatorType} based validator set at block {block.ToString(Block.Format.Short)}.");
                }

                _lastProcessedBlock = block.Number;
            }
        }

        public void SetFinalizationManager(IAuRaBlockFinalizationManager finalizationManager, BlockHeader parentHeader)
        {
            _blockFinalizationManager = finalizationManager ?? throw new ArgumentNullException(nameof(finalizationManager));
            _blockFinalizationManager.BlocksFinalized += OnBlocksFinalized;
            InitCurrentValidator(_blockFinalizationManager.LastFinalizedBlockLevel, parentHeader);
        }

        public void Dispose()
        {
            _blockFinalizationManager.BlocksFinalized -= OnBlocksFinalized;
        }

        private void SetCurrentValidator(KeyValuePair<long, AuRaParameters.Validator> validatorInfo, BlockHeader parentHeader)
        {
            SetCurrentValidator(validatorInfo.Key, validatorInfo.Value, parentHeader);
        }

        private void SetCurrentValidator(long finalizedAtBlockNumber, AuRaParameters.Validator validatorPrototype, BlockHeader parentHeader)
        {
            if (validatorPrototype != _currentValidatorPrototype)
            {
                (_currentValidator as IDisposable)?.Dispose();
                _currentValidator = CreateValidator(finalizedAtBlockNumber, validatorPrototype, parentHeader);
                _currentValidatorPrototype = validatorPrototype;

                if (!_forSealing)
                {
                    if (_currentValidator.Validators is not null)
                    {
                        _validatorStore.SetValidators(finalizedAtBlockNumber, _currentValidator.Validators);
                    }
                    else if (_blockTree.Head is not null)
                    {
                        if (_logger.IsWarn) _logger.Warn($"Validators not found in validator initialized at block {finalizedAtBlockNumber}, even after genesis block loaded.");
                    }
                }
            }
        }

        private IAuRaValidator CreateValidator(long finalizedAtBlockNumber, AuRaParameters.Validator validatorPrototype, BlockHeader parentHeader) =>
            _validatorFactory.CreateValidatorProcessor(validatorPrototype, parentHeader, finalizedAtBlockNumber + 1);

        public void ReportMalicious(Address validator, long blockNumber, byte[] proof, IReportingValidator.MaliciousCause cause)
        {
            _currentValidator.GetReportingValidator().ReportMalicious(validator, blockNumber, proof, cause);
        }

        public void ReportBenign(Address validator, long blockNumber, IReportingValidator.BenignCause cause)
        {
            _currentValidator.GetReportingValidator().ReportBenign(validator, blockNumber, cause);
        }

        public void TryReportSkipped(BlockHeader header, BlockHeader parent)
        {
            _currentValidator.GetReportingValidator().TryReportSkipped(header, parent);
        }

        public IEnumerable<Transaction> GetTransactions(BlockHeader parent, long gasLimit, PayloadAttributes? payloadAttributes) =>
            _currentValidator is ITxSource txSource
                ? txSource.GetTransactions(parent, gasLimit, payloadAttributes)
                : Enumerable.Empty<Transaction>();

        public override string ToString() => $"{nameof(MultiValidator)} [ {(_currentValidator is ITxSource txSource ? txSource.ToString() : string.Empty)} ]";

    }
}
