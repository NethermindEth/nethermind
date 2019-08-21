using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Abi;
using Nethermind.AuRa.Contracts;
using Nethermind.Core;
using Nethermind.Core.Specs.ChainSpecStyle;
using Nethermind.Core.Specs.Forks;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.Store;

namespace Nethermind.AuRa.Validators
{
    public class ContractValidator : IAuRaValidatorProcessor
    {
        private ValidatorContract _validatorContract;
        private readonly ILogger _logger;
        private readonly IStateProvider _stateProvider;
        private readonly ITransactionProcessor _transactionProcessor;
        
        private HashSet<Address> _validators;
        private PendingValidators _pendingValidators;
        private readonly HashSet<Address> _validatedSinceFinalized = new HashSet<Address>();

        protected Address ContractAddress { get; }
        protected IAbiEncoder AbiEncoder { get; }
        protected long StartBlockNumber { get; }
        protected CallOutputTracer Output { get; } = new CallOutputTracer();
        protected ValidatorContract ValidatorContract => _validatorContract ?? (_validatorContract = CreateValidatorContract(ContractAddress));
        private bool IsInitialized => _validators != null;

        public ContractValidator(
            AuRaParameters.Validator validator,
            IStateProvider stateProvider,
            IAbiEncoder abiEncoder,
            ITransactionProcessor transactionProcessor,
            ILogManager logManager,            
            long startBlockNumber)
        {
            if (validator == null) throw new ArgumentNullException(nameof(validator));
            if (validator.ValidatorType != Type) 
                throw new ArgumentException("Wrong validator type.", nameof(validator));
            
            ContractAddress = validator.Addresses?.FirstOrDefault() ?? throw new ArgumentException("Missing contract address for AuRa validator.", nameof(validator.Addresses));
            _stateProvider = stateProvider ?? throw new ArgumentNullException(nameof(stateProvider));
            _transactionProcessor = transactionProcessor ?? throw new ArgumentNullException(nameof(transactionProcessor));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            AbiEncoder = abiEncoder ?? throw new ArgumentNullException(nameof(abiEncoder));
            StartBlockNumber = startBlockNumber;
        }
        
        public bool IsValidSealer(Address address) => _validators.Contains(address);

        public virtual void PreProcess(Block block)
        {
            if (!IsInitialized)
            {
                Initialize(block);
            }

            FinalizePendingValidatorsIfNeeded(block);
        }

        private void FinalizePendingValidatorsIfNeeded(Block block)
        {
            bool thereArePendingValidators = _pendingValidators != null;
            if (thereArePendingValidators)
            {
                var shouldFinalizePendingValidators = _validatedSinceFinalized.Count > 0 && _validatedSinceFinalized.Count > _validators.Count / 2;
                if (shouldFinalizePendingValidators)
                {
                    if (_logger.IsInfo) _logger.Info($"Applying validator set change signalled at block {_pendingValidators.BlockNumber} at block {block.Number}.");
                    var transaction = ValidatorContract.FinalizeChange();
                    SystemContract.InvokeTransaction(block.Header, _transactionProcessor, transaction, Output);
                    
                    _validators = new HashSet<Address>(_pendingValidators.Addresses);
                    _pendingValidators = null;
                    _validatedSinceFinalized.Clear();
                }
                else
                {
                    _validatedSinceFinalized.Add(block.Beneficiary);                    
                }
            }
        }

        public virtual void PostProcess(Block block, TxReceipt[] receipts)
        {
            if (ValidatorContract.CheckInitiateChangeEvent(ContractAddress, block, receipts, out var potentialValidators))
            {
                InitiateChange(block, potentialValidators);
                if (_logger.IsInfo) _logger.Info($"Signal for transition within contract at block {block.Number}. New list: [{string.Join<Address>(", ", potentialValidators)}].");
            }
        }

        public virtual AuRaParameters.ValidatorType Type => AuRaParameters.ValidatorType.Contract;
        
        protected virtual ValidatorContract CreateValidatorContract(Address contractAddress)
        {
            return new ValidatorContract(AbiEncoder, contractAddress);
        }

        private void Initialize(Block block)
        {
            var startBlockInitialize = StartBlockNumber == block.Number;
            
            if (startBlockInitialize)
            {
                CreateSystemAccount();
            }
            
            // Todo if last InitiateChange is not finalized we need to load potential validators.
            var validators = LoadValidatorsFromContract(block);
            
            if (startBlockInitialize)
            {
                InitiateChange(block, validators);
                if(_logger.IsInfo) _logger.Info($"Signal for switch to contract based validator set at block {block.Number}. Initial contract validators: [{string.Join<Address>(", ", validators)}].");
            }
        }

        private void InitiateChange(Block block, Address[] potentialValidators)
        {
            // We are ignoring the signal if there are already pending validators. This replicates Parity behaviour which can be seen as a bug.
            if (_pendingValidators == null && potentialValidators.Length > 0)
            {
                _pendingValidators = new PendingValidators(block.Number, potentialValidators);
                _validatedSinceFinalized.Add(block.Beneficiary);
            }
        }

        private Address[] LoadValidatorsFromContract(Block block)
        {
            SystemContract.InvokeTransaction(block.Header, _transactionProcessor, ValidatorContract.GetValidators(), Output);

            var validators = ValidatorContract.DecodeAddresses(Output.ReturnValue);
            if (validators.Length == 0)
            {
                throw new AuRaException("Failed to initialize validators list.");
            }

            _validators = validators.ToHashSet();
            return validators;
        }


        private void CreateSystemAccount()
        {
            _stateProvider.CreateAccount(Address.SystemUser, UInt256.Zero);
            _stateProvider.Commit(Homestead.Instance);
        }

        private class PendingValidators
        {
            public PendingValidators(long blockNumber, Address[] addresses)
            {
                BlockNumber = blockNumber;
                Addresses = addresses;
            }

            public Address[] Addresses { get; }
            
            public long BlockNumber { get; }
        }
    }
}