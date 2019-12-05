/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Specs.ChainSpecStyle;
using Nethermind.Logging;

namespace Nethermind.AuRa.Validators
{
    public class MultiValidator : IAuRaValidatorProcessor
    {
        private readonly IAuRaAdditionalBlockProcessorFactory _validatorFactory;
        private IBlockFinalizationManager _blockFinalizationManager;
        private readonly IDictionary<long, AuRaParameters.Validator> _validators;
        private readonly ILogger _logger;
        private IAuRaValidatorProcessor _currentValidator;
        private bool _isProducing;
        private long _lastProcessedBlock = 0;

        public MultiValidator(AuRaParameters.Validator validator, IAuRaAdditionalBlockProcessorFactory validatorFactory, ILogManager logManager)
        {
            if (validator == null) throw new ArgumentNullException(nameof(validator));
            if (validator.ValidatorType != AuRaParameters.ValidatorType.Multi) throw new ArgumentException("Wrong validator type.", nameof(validator));
            _validatorFactory = validatorFactory ?? throw new ArgumentNullException(nameof(validatorFactory));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            
            _validators = validator.Validators?.Count > 0
                ? validator.Validators
                : throw new ArgumentException("Multi validator cannot be empty.", nameof(validator.Validators));
        }

        private void InitCurrentValidator(long blockNumber)
        {
            TrySetLastValidator(blockNumber, true);
            _lastProcessedBlock = blockNumber;
        }
        
        private bool TryGetLastValidator(long blockNum, out KeyValuePair<long, AuRaParameters.Validator>? val)
        {
            val = null;
            
            foreach (var kvp in _validators)
            {
                if (kvp.Key <= blockNum)
                {
                    val = kvp;
                }
            }
            
            return val != null;
        }

        private void OnBlocksFinalized(object sender, FinalizeEventArgs e)
        {
            for (int i = 0; i < e.FinalizedBlocks.Count; i++)
            {
                var finalizedBlockHeader = e.FinalizedBlocks[i];
                if (TryGetValidator(finalizedBlockHeader.Number, out var validator) && !validator.ValidatorType.CanChangeImmediately())
                {
                    SetCurrentValidator(e.FinalizingBlock.Number, validator);
                    if (_logger.IsInfo && !_isProducing) _logger.Info($"Applying chainspec validator change signalled at block {finalizedBlockHeader.ToString(BlockHeader.Format.Short)} at block {e.FinalizingBlock.ToString(BlockHeader.Format.Short)}.");
                }
            }
        }
        
        public void PreProcess(Block block, ProcessingOptions options = ProcessingOptions.None)
        {
            bool isProducingBlock = options.IsProducingBlock();
            long previousBlockNumber = block.Number - 1;
            bool isNotConsecutive = previousBlockNumber != _lastProcessedBlock;

            if (isProducingBlock || isNotConsecutive)
            {
                var result = TrySetLastValidator(previousBlockNumber, false);
                if (!result.ValidatorSet && result.Validator.HasValue)
                {
                    var validatorInfo = result.Validator.Value;
                    var lastFinalizedBlock = _blockFinalizationManager.GetLastFinalizedBy(previousBlockNumber);
                    if (lastFinalizedBlock >= validatorInfo.Key)
                    {
                        SetCurrentValidator(previousBlockNumber, validatorInfo.Value);
                    }
                    else if (lastFinalizedBlock != _lastProcessedBlock)
                    {
                        TrySetLastValidator(lastFinalizedBlock, true);
                    }
                }
            }

            _currentValidator?.PreProcess(block, options);
        }

        private (bool ValidatorSet, KeyValuePair<long, AuRaParameters.Validator>? Validator) TrySetLastValidator(long processedBlockNumber, bool forceImmediateChange)
        {
            if (TryGetLastValidator(processedBlockNumber, out var validatorInfo))
            {
                var validator = validatorInfo.Value;
                if (forceImmediateChange || validator.Value.ValidatorType.CanChangeImmediately())
                {
                    SetCurrentValidator(validator.Key, validator.Value);
                    return (true, validatorInfo);
                }
                
                return (false, validatorInfo);
            }
            
            return (false, null);
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
                    if (_logger.IsInfo && notProducing) _logger.Info($"Immediately applying chainspec validator change signalled at block at block {block.ToString(Block.Format.Short)} to {validator.ValidatorType}.");
                }
                else if (_logger.IsInfo && notProducing) _logger.Info($"Signal for switch to chainspec {validator.ValidatorType} based validator set at block {block.ToString(Block.Format.Short)}.");
            }

            _lastProcessedBlock = block.Number;
        }
        
        public bool IsValidSealer(Address address, long step) => _currentValidator?.IsValidSealer(address, step) == true;

        public int MinSealersForFinalization => _currentValidator.MinSealersForFinalization;
        public int CurrentSealersCount => _currentValidator.CurrentSealersCount;

        void IAuRaValidator.SetFinalizationManager(IBlockFinalizationManager finalizationManager, bool forProducing)
        {
            if (_blockFinalizationManager != null)
            {
                _blockFinalizationManager.BlocksFinalized -= OnBlocksFinalized;
            }

            _blockFinalizationManager = finalizationManager;
            _isProducing = forProducing;
            
            if (_blockFinalizationManager != null)
            {
                _blockFinalizationManager.BlocksFinalized += OnBlocksFinalized;
                InitCurrentValidator(_blockFinalizationManager.LastFinalizedBlockLevel);
            }

            _currentValidator?.SetFinalizationManager(finalizationManager, forProducing);
        }

        private void SetCurrentValidator(long finalizedAtBlockNumber, AuRaParameters.Validator validator)
        {
            _currentValidator?.SetFinalizationManager(null);
            _currentValidator = _validatorFactory.CreateValidatorProcessor(validator, finalizedAtBlockNumber + 1);
            _currentValidator.SetFinalizationManager(_blockFinalizationManager, _isProducing);
        }
    }
}