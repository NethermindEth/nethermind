using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Specs.ChainSpecStyle;
using Nethermind.Evm;
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
                        validatorFactory.CreateValidatorProcessor(kvp.Value, Math.Max(1, kvp.Key)))) // we need to make init block at least 1.
                    .ToArray()
                : throw new ArgumentException("Multi validator cannot be empty.", nameof(validator.Validators));

            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        public void PreProcess(Block block, ITransactionProcessor transactionProcessor)
        {
            if (TryUpdateValidator(block))
            {
                if (_logger.IsInfo) _logger.Info($"Signal for switch to {_currentValidator.Type} based validator set at block {block.Number}.");
            }
            _currentValidator?.PreProcess(block, transactionProcessor);
        }

        public void PostProcess(Block block, TxReceipt[] receipts, ITransactionProcessor transactionProcessor)
        {
            _currentValidator?.PostProcess(block, receipts, transactionProcessor);
        }
        
        public bool IsValidSealer(Address address) => _currentValidator?.IsValidSealer(address) == true;
        
        private bool TryUpdateValidator(Block block)
        {
            var result = false;
            
            while (_validators.Length > _nextValidator && block.Number >= _validators[_nextValidator].Key)
            {
                _currentValidator = _validators[_nextValidator].Value;
                _nextValidator++;
                result = true;
            }

            return result;
        }

        public AuRaParameters.ValidatorType Type => AuRaParameters.ValidatorType.Multi;
    }
}