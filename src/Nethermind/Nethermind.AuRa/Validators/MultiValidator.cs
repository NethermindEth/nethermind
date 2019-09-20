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
        private IAuRaValidatorProcessor _currentValidator = null;
        
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

        private void InitCurrentValidator()
        {
            var lastFinalized = _blockFinalizationManager.LastFinalizedBlockLevel;
            var validator = _validators.TakeWhile(kvp => lastFinalized >= kvp.Key).Select(x => x.Value).LastOrDefault();
            if (validator != null)
            {
                SetCurrentValidator(lastFinalized, validator);
            }
        }

        private void OnBlocksFinalized(object sender, FinalizeEventArgs e)
        {
            for (int i = 0; i < e.FinalizedBlocks.Count; i++)
            {
                var finalizedBlockHeader = e.FinalizedBlocks[i];
                if (TryUpdateValidator(finalizedBlockHeader.Number, e.FinalizingBlock.Number))
                {
                    _logger.Info($"Applying chainspec validator change signalled at block {finalizedBlockHeader.Number} at block {e.FinalizingBlock.Number}.");
                }
            }
        }
        
        public void PreProcess(Block block)
        {
            if (_logger.IsInfo)
            {
                if (TryGetValidator(block.Header.Number, out var validator))
                {
                    _logger.Info($"Signal for switch to chainspec {validator.ValidatorType} based validator set at block {block.Number}.");
                }

            }
            _currentValidator?.PreProcess(block);
        }

        private bool TryGetValidator(long blockNumber, out AuRaParameters.Validator validator) => 
            _validators.TryGetValue(blockNumber, out validator);

        public void PostProcess(Block block, TxReceipt[] receipts)
        {
            _currentValidator?.PostProcess(block, receipts);
        }
        
        public bool IsValidSealer(Address address) => _currentValidator?.IsValidSealer(address) == true;
        
        public int MinSealersForFinalization => _currentValidator.MinSealersForFinalization;

        void IAuRaValidator.SetFinalizationManager(IBlockFinalizationManager finalizationManager)
        {
            if (_blockFinalizationManager != null)
            {
                _blockFinalizationManager.BlocksFinalized -= OnBlocksFinalized;
            }

            _blockFinalizationManager = finalizationManager;
            
            if (_blockFinalizationManager != null)
            {
                _blockFinalizationManager.BlocksFinalized += OnBlocksFinalized;
                InitCurrentValidator();
            }

            _currentValidator?.SetFinalizationManager(finalizationManager);
        }

        private bool TryUpdateValidator(long finalizedBlockHeaderNumber, long finalizedAtBlockNumber)
        {
            if (TryGetValidator(finalizedBlockHeaderNumber, out var validator))
            {
                SetCurrentValidator(finalizedAtBlockNumber, validator);
                return true;
            }

            return false;
        }

        private void SetCurrentValidator(long finalizedAtBlockNumber, AuRaParameters.Validator validator)
        {
            _currentValidator?.SetFinalizationManager(null);
            _currentValidator = _validatorFactory.CreateValidatorProcessor(validator, finalizedAtBlockNumber + 1);
            _currentValidator.SetFinalizationManager(_blockFinalizationManager);
        }

        public AuRaParameters.ValidatorType Type => AuRaParameters.ValidatorType.Multi;
    }
}