using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Specs.ChainSpecStyle;
using Nethermind.Evm;

namespace Nethermind.AuRa.Validators
{
    public class ListValidator : IAuRaValidatorProcessor
    {
        private readonly ISet<Address> _validatorAddresses;

        public ListValidator(AuRaParameters.Validator validator)
        {
            if (validator == null) throw new ArgumentNullException(nameof(validator));
            if (validator.ValidatorType != AuRaParameters.ValidatorType.List) 
                throw new ArgumentException("Wrong validator type.", nameof(validator));
            
            _validatorAddresses = validator.Addresses?.Length > 0
                ? validator.Addresses.ToHashSet()
                : throw new ArgumentException("Empty validator Addresses.", nameof(validator.Addresses));
        }
        
        public void PreProcess(Block block, ITransactionProcessor transactionProcessor) { }

        public void PostProcess(Block block, TxReceipt[] receipts, ITransactionProcessor transactionProcessor) { }

        public void Initialize(Block block, TransactionProcessor transactionProcessor) { }

        public bool IsValidSealer(Address address) => _validatorAddresses.Contains(address);
        public AuRaParameters.ValidatorType Type => AuRaParameters.ValidatorType.List;
    }
}