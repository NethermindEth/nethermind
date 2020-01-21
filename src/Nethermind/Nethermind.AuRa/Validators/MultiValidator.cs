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
using System.Diagnostics;
using System.Linq;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Logging;

namespace Nethermind.AuRa.Validators
{
    public class MultiValidator : IAuRaValidatorProcessor
    {
        private readonly IAuRaAdditionalBlockProcessorFactory _validatorFactory;
        private readonly IBlockTree _blockTree;
        private readonly IValidatorStore _validatorStore;
        private IBlockFinalizationManager _blockFinalizationManager;
        private readonly IDictionary<long, AuRaParameters.Validator> _validators;
        private readonly ILogger _logger;
        private IAuRaValidatorProcessor _currentValidator;
        private AuRaParameters.Validator _currentValidatorPrototype;
        private bool _validatorUsedForSealing;
        private long _lastProcessedBlock = 0;
        
        public MultiValidator(AuRaParameters.Validator validator, IAuRaAdditionalBlockProcessorFactory validatorFactory, IBlockTree blockTree, IValidatorStore validatorStore, ILogManager logManager)
        {
            if (validator == null) throw new ArgumentNullException(nameof(validator));
            if (validator.ValidatorType != AuRaParameters.ValidatorType.Multi) throw new ArgumentException("Wrong validator type.", nameof(validator));
            _validatorFactory = validatorFactory ?? throw new ArgumentNullException(nameof(validatorFactory));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _validatorStore = validatorStore ?? throw new ArgumentNullException(nameof(validatorStore));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            
            _validators = validator.Validators?.Count > 0
                ? validator.Validators
                : throw new ArgumentException("Multi validator cannot be empty.", nameof(validator.Validators));
        }
        
        public Address[] Validators => _currentValidator?.Validators;

        private void InitCurrentValidator(long blockNumber)
        {
            if (TryGetLastValidator(blockNumber, out var validatorInfo))
            {
                SetCurrentValidator(validatorInfo);
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
                    SetCurrentValidator(e.FinalizingBlock.Number, validator);
                    if (!_validatorUsedForSealing)
                    {
                        if (_logger.IsInfo) _logger.Info($"Applying chainspec validator change signalled at block {finalizedBlockHeader.ToString(BlockHeader.Format.Short)} at block {e.FinalizingBlock.ToString(BlockHeader.Format.Short)}.");
                    }
                }
            }
        }
        
        public void PreProcess(Block block, ProcessingOptions options = ProcessingOptions.None)
        {
            bool ValidatorWasAlreadyFinalized(KeyValuePair<long, AuRaParameters.Validator> validatorInfo) => _blockFinalizationManager.LastFinalizedBlockLevel >= validatorInfo.Key;

            bool isProducingBlock = options.IsProducingBlock();
            long previousBlockNumber = block.Number - 1;
            bool isNotConsecutive = previousBlockNumber != _lastProcessedBlock;

            if (isProducingBlock || isNotConsecutive)
            {
                if (TryGetLastValidator(previousBlockNumber, out var validatorInfo))
                {
                    if (validatorInfo.Value.ValidatorType.CanChangeImmediately() || ValidatorWasAlreadyFinalized(validatorInfo))
                    {
                        SetCurrentValidator(validatorInfo);
                    }
                    else if (!isProducingBlock)
                    {
                        bool canSetValidatorAsCurrent = !TryGetLastValidator(validatorInfo.Key - 1, out var previousValidatorInfo);
                        long? finalizedAtBlockNumber = null;
                        if (!canSetValidatorAsCurrent)
                        {
                            SetCurrentValidator(previousValidatorInfo);
                            finalizedAtBlockNumber = _blockFinalizationManager.GetFinalizedLevel(validatorInfo.Key);
                            canSetValidatorAsCurrent = finalizedAtBlockNumber != null;
                        }
                    
                        if (canSetValidatorAsCurrent)
                        {
                            SetCurrentValidator(finalizedAtBlockNumber ?? validatorInfo.Key, validatorInfo.Value);
                        }
                    }
                }
            }

            _currentValidator?.PreProcess(block, options);
        }

        private bool TryGetValidator(long blockNumber, out AuRaParameters.Validator validator) => _validators.TryGetValue(blockNumber, out validator);
        
        public void PostProcess(Block block, TxReceipt[] receipts, ProcessingOptions options = ProcessingOptions.None)
        {
            _currentValidator?.PostProcess(block, receipts, options);

            var notProducing = !options.IsProducingBlock();

            if (TryGetValidator(block.Number, out var validator))
            {
                if (validator.ValidatorType.CanChangeImmediately())
                {
                    SetCurrentValidator(block.Number, validator);
                    if (_logger.IsInfo && notProducing) _logger.Info($"Immediately applying chainspec validator change signalled at block {block.ToString(Block.Format.Short)} to {validator.ValidatorType}.");
                }
                else if (_logger.IsInfo && notProducing) _logger.Info($"Signal for switch to chainspec {validator.ValidatorType} based validator set at block {block.ToString(Block.Format.Short)}.");
            }

            _lastProcessedBlock = block.Number;
        }

        void IAuRaValidator.SetFinalizationManager(IBlockFinalizationManager finalizationManager, bool forProducing)
        {
            if (_blockFinalizationManager != null)
            {
                _blockFinalizationManager.BlocksFinalized -= OnBlocksFinalized;
            }

            _blockFinalizationManager = finalizationManager;
            _validatorUsedForSealing = forProducing;
            
            if (_blockFinalizationManager != null)
            {
                _blockFinalizationManager.BlocksFinalized += OnBlocksFinalized;
                InitCurrentValidator(_blockFinalizationManager.LastFinalizedBlockLevel);
            }

            _currentValidator?.SetFinalizationManager(finalizationManager, forProducing);
        }

        private void SetCurrentValidator(KeyValuePair<long, AuRaParameters.Validator> validatorInfo)
        {
            SetCurrentValidator(validatorInfo.Key, validatorInfo.Value);
        }
        
        private void SetCurrentValidator(long finalizedAtBlockNumber, AuRaParameters.Validator validatorPrototype)
        {
            if (validatorPrototype != _currentValidatorPrototype)
            {
                _currentValidator?.SetFinalizationManager(null);
                _currentValidator = CreateValidator(finalizedAtBlockNumber, validatorPrototype);
                _currentValidator.SetFinalizationManager(_blockFinalizationManager, _validatorUsedForSealing);
                _currentValidatorPrototype = validatorPrototype;
                
                if (!_validatorUsedForSealing)
                {
                    _validatorStore.SetValidators(finalizedAtBlockNumber, _currentValidator.Validators);
                }
            }
        }

        private IAuRaValidatorProcessor CreateValidator(long finalizedAtBlockNumber, AuRaParameters.Validator validatorPrototype)
        {
            return _validatorFactory.CreateValidatorProcessor(validatorPrototype, finalizedAtBlockNumber + 1);
        }
    }
}