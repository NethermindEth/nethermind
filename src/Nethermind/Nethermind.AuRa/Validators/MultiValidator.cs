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
using Nethermind.Core;
using Nethermind.Core.Specs.ChainSpecStyle;
using Nethermind.Logging;

namespace Nethermind.AuRa.Validators
{
    public class MultiValidator : IAuRaValidatorProcessor
    {
        private readonly KeyValuePair<long, IAuRaValidatorProcessor>[] _validators;
        private readonly ILogger _logger;
        
        private IAuRaValidatorProcessor _currentValidator = null;
        private int _nextValidator = 0;

        public MultiValidator(AuRaParameters.Validator validator, IAuRaAdditionalBlockProcessorFactory validatorFactory, ILogManager logManager)
        {
            if (validator == null) throw new ArgumentNullException(nameof(validator));
            if (validatorFactory == null) throw new ArgumentNullException(nameof(validatorFactory));
            if (validator.ValidatorType != AuRaParameters.ValidatorType.Multi) 
                throw new ArgumentException("Wrong validator type.", nameof(validator));
            
            _validators = validator.Validators?.Count > 0
                ? validator.Validators
                    .Select(kvp => new KeyValuePair<long, IAuRaValidatorProcessor>(kvp.Key,
                        validatorFactory.CreateValidatorProcessor(kvp.Value, Math.Max(1, kvp.Key)))) // we need to make init block at least 1 as 0 is genesis.
                    .ToArray()
                : throw new ArgumentException("Multi validator cannot be empty.", nameof(validator.Validators));

            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        public void PreProcess(Block block)
        {
            if (TryUpdateValidator(block))
            {
                if (_logger.IsInfo) _logger.Info($"Signal for switch to {_currentValidator.Type} based validator set at block {block.Number}.");
            }
            _currentValidator?.PreProcess(block);
        }

        public void PostProcess(Block block, TxReceipt[] receipts)
        {
            _currentValidator?.PostProcess(block, receipts);
        }
        
        public bool IsValidSealer(Address address) => _currentValidator?.IsValidSealer(address) == true;
        
        private bool TryUpdateValidator(Block block)
        {
            var validatorUpdated = false;
            
            // Check next validators as blocks are proceeding
            while (_validators.Length > _nextValidator && block.Number >= _validators[_nextValidator].Key)
            {
                _currentValidator = _validators[_nextValidator].Value;
                _nextValidator++;
                validatorUpdated = true;
            }

            // Check previous validators if reorganisation happened
            var currentValidator = _nextValidator - 1;
            while (currentValidator >= 0 && block.Number < _validators[currentValidator].Key)
            {
                _nextValidator = currentValidator;
                currentValidator--;
                _currentValidator = _validators[currentValidator].Value;
                validatorUpdated = true;
            }

            return validatorUpdated;
        }

        public AuRaParameters.ValidatorType Type => AuRaParameters.ValidatorType.Multi;
    }
}