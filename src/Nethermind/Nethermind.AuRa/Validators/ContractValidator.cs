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
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Nethermind.Abi;
using Nethermind.AuRa.Contracts;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Extensions;
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
        private readonly IBlockTree _blockTree;

        private Address[] _validators;
        private PendingValidators _pendingValidators;
        private readonly ValidationStampCollection _validatedSinceFinalized = new ValidationStampCollection();

        protected Address ContractAddress { get; }
        protected IAbiEncoder AbiEncoder { get; }
        protected long StartBlockNumber { get; }
        private long LastProcessedBlockNumber { get; set; } = -1;
        protected CallOutputTracer Output { get; } = new CallOutputTracer();
        protected ValidatorContract ValidatorContract => _validatorContract ?? (_validatorContract = CreateValidatorContract(ContractAddress));
        private bool IsInitialized => LastProcessedBlockNumber > 0;

        public ContractValidator(
            AuRaParameters.Validator validator,
            IStateProvider stateProvider,
            IAbiEncoder abiEncoder,
            ITransactionProcessor transactionProcessor,
            IBlockTree blockTree,
            ILogManager logManager,            
            long startBlockNumber)
        {
            if (validator == null) throw new ArgumentNullException(nameof(validator));
            if (validator.ValidatorType != Type) 
                throw new ArgumentException("Wrong validator type.", nameof(validator));
            
            ContractAddress = validator.Addresses?.FirstOrDefault() ?? throw new ArgumentException("Missing contract address for AuRa validator.", nameof(validator.Addresses));
            _stateProvider = stateProvider ?? throw new ArgumentNullException(nameof(stateProvider));
            _transactionProcessor = transactionProcessor ?? throw new ArgumentNullException(nameof(transactionProcessor));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            AbiEncoder = abiEncoder ?? throw new ArgumentNullException(nameof(abiEncoder));
            StartBlockNumber = startBlockNumber;
        }
        
        public bool IsValidSealer(Address address, ulong step)
        {
            if (_blockTree.Head.Number + 1 == StartBlockNumber)
            {
                
                LazyInitializer.EnsureInitialized(ref _validators, () => LoadValidatorsFromContract(_blockTree.Head));
            }
            
            return _validators.GetItemRoundRobin(step) == address;
        }

        public virtual void PreProcess(Block block)
        {
            var blockHeader = block.Header;
            var isStartBlock = StartBlockNumber == blockHeader.Number;
            if (isStartBlock || !IsInitialized)
            {
                Initialize(blockHeader, isStartBlock);
            }
            else
            {
                ClearPendingValidatorsAfterReorganisation(blockHeader);                
            }

            FinalizePendingValidatorsIfNeeded(blockHeader);
        }

        private void ClearPendingValidatorsAfterReorganisation(BlockHeader blockHeader)
        {
            bool reorganisationHappened = blockHeader.Number <= _validatedSinceFinalized.LastBlockNumber;
            if (reorganisationHappened)
            {
                bool reorganisationBeforeInitChange = blockHeader.Number <= _pendingValidators.BlockNumber;
                if (reorganisationBeforeInitChange)
                {
                    ClearPendingValidators();
                }
                else
                {
                    _validatedSinceFinalized.RemoveFromBlock(blockHeader.Number);
                }
            }
        }

        private void FinalizePendingValidatorsIfNeeded(BlockHeader blockHeader)
        {
            bool thereArePendingValidators = _pendingValidators != null;
            if (thereArePendingValidators)
            {
                var shouldFinalizePendingValidators = _validatedSinceFinalized.Count > 0 && _validatedSinceFinalized.Count > _validators.Length / 2;
                if (shouldFinalizePendingValidators)
                {
                    if (_logger.IsInfo) _logger.Info($"Applying validator set change signalled at block {_pendingValidators.BlockNumber} at block {blockHeader.Number}.");
                    ValidatorContract.InvokeTransaction(blockHeader, _transactionProcessor, ValidatorContract.FinalizeChange(), Output);
                    
                    _validators = _pendingValidators.Addresses;
                    ClearPendingValidators();
                }
                else
                {
                    _validatedSinceFinalized.Add(new ValidationStamp(blockHeader));                    
                }
            }
        }

        public virtual void PostProcess(Block block, TxReceipt[] receipts)
        {
            var blockHeader = block.Header;
            LastProcessedBlockNumber = blockHeader.Number;
            if (ValidatorContract.CheckInitiateChangeEvent(ContractAddress, blockHeader, receipts, out var potentialValidators))
            {
                InitiateChange(blockHeader, potentialValidators);
                if (_logger.IsInfo) _logger.Info($"Signal for transition within contract at block {blockHeader.Number}. New list: [{string.Join<Address>(", ", potentialValidators)}].");
            }
        }

        public virtual AuRaParameters.ValidatorType Type => AuRaParameters.ValidatorType.Contract;
        
        protected virtual ValidatorContract CreateValidatorContract(Address contractAddress) => 
            new ValidatorContract(AbiEncoder, contractAddress);

        private void Initialize(BlockHeader blockHeader, bool isStartBlock)
        {
            if (isStartBlock)
            {
                CreateSystemAccount();
            }
            
            // Todo if last InitiateChange is not finalized we need to load potential validators.
            var validators = LoadValidatorsFromContract(blockHeader);
            
            if (isStartBlock)
            {
                InitiateChange(blockHeader, validators);
                if(_logger.IsInfo) _logger.Info($"Signal for switch to contract based validator set at block {blockHeader.Number}. Initial contract validators: [{string.Join<Address>(", ", validators)}].");
            }
        }

        private void InitiateChange(BlockHeader blockHeader, Address[] potentialValidators)
        {
            // We are ignoring the signal if there are already pending validators. This replicates Parity behaviour which can be seen as a bug.
            if (_pendingValidators == null && potentialValidators.Length > 0)
            {
                _pendingValidators = new PendingValidators(blockHeader.Number, potentialValidators);
                _validatedSinceFinalized.Add(new ValidationStamp(blockHeader));
            }
        }

        private Address[] LoadValidatorsFromContract(BlockHeader blockHeader)
        {
            ValidatorContract.InvokeTransaction(blockHeader, _transactionProcessor, ValidatorContract.GetValidators(), Output);

            var validators = ValidatorContract.DecodeAddresses(Output.ReturnValue);
            if (validators.Length == 0)
            {
                throw new AuRaException("Failed to initialize validators list.");
            }

            _validators = validators;
            return validators;
        }


        private void CreateSystemAccount()
        {
            if (!_stateProvider.AccountExists(Address.SystemUser))
            {
                _stateProvider.CreateAccount(Address.SystemUser, UInt256.Zero);
                _stateProvider.Commit(Homestead.Instance);
            }
        }
        
        private void ClearPendingValidators()
        {
            _pendingValidators = null;
            _validatedSinceFinalized.Clear();
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
        
        private class ValidationStamp : IEquatable<ValidationStamp>
        {

            public ValidationStamp(long blockNumber, Address address)
            {
                BlockNumber = blockNumber;
                Address = address;
            }

            public long BlockNumber { get; }
            
            public Address Address { get; }

            public ValidationStamp(BlockHeader block) : this(block.Number, block.Beneficiary) { }

            public bool Equals(ValidationStamp other)
            {
                if (ReferenceEquals(null, other)) return false;
                if (ReferenceEquals(this, other)) return true;
                return Equals(Address, other.Address);
            }
            
            public override bool Equals(object obj)
            {
                if (ReferenceEquals(null, obj)) return false;
                if (ReferenceEquals(this, obj)) return true;
                if (obj.GetType() != GetType()) return false;
                return Equals((ValidationStamp) obj);
            }

            public override int GetHashCode()
            {
                return Address?.GetHashCode() ?? 0;
            }
        }
        
        private class ValidationStampCollection
        {
            private readonly ISet<ValidationStamp> _set;
            private readonly List<ValidationStamp> _list;

            public ValidationStampCollection() : this(new HashSet<ValidationStamp>()) { }

            public ValidationStampCollection(ISet<ValidationStamp> set)
            {
                _set = set;
                _list = new List<ValidationStamp>(set);
            }

            public int Count => _set.Count;

            public long? LastBlockNumber => _list.LastOrDefault()?.BlockNumber;

            public void Add(ValidationStamp item)
            {
                if (_set.Add(item))
                {
                    _list.Add(item);                    
                }
            }

            public void Clear()
            {
                _list.Clear();
                _set.Clear();
            }

            public void RemoveFromBlock(long blockNumber)
            {
                var removeFromIndex = _list.FindLastIndex(i => i.BlockNumber < blockNumber) + 1;
                for (int i = removeFromIndex; i < _list.Count; i++)
                {
                    _set.Remove(_list[i]);
                }
                
                _list.RemoveRange(removeFromIndex, _list.Count - removeFromIndex);
            }
        }
    }
}