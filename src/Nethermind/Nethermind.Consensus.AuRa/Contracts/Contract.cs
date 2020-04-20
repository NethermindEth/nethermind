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
using Nethermind.Abi;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.Store;

namespace Nethermind.Consensus.AuRa.Contracts
{
    public class Contract
    {
        public const long DefaultContractGasLimit = 50000000L;
        private readonly ITransactionProcessor _transactionProcessor;
        protected IAbiEncoder AbiEncoder { get; }
        protected Address ContractAddress { get; }

        protected Contract(ITransactionProcessor transactionProcessor, IAbiEncoder abiEncoder, Address contractAddress)
        {
            _transactionProcessor = transactionProcessor ?? throw new ArgumentNullException(nameof(transactionProcessor));
            AbiEncoder = abiEncoder ?? throw new ArgumentNullException(nameof(abiEncoder));
            ContractAddress = contractAddress ?? throw new ArgumentNullException(nameof(contractAddress));
        }
        
        protected Transaction GenerateTransaction(byte[] transactionData, Address sender, long gasLimit = DefaultContractGasLimit, UInt256? nonce = null)
        {
            var transaction = new Transaction(true)
            {
                Value = UInt256.Zero,
                Data = transactionData,
                To = ContractAddress,
                SenderAddress = sender ?? Address.SystemUser,
                GasLimit = gasLimit,
                GasPrice = UInt256.Zero,
                Nonce = nonce ?? UInt256.Zero,
            };
                
            transaction.Hash = transaction.CalculateHash();

            return transaction;
        }

        protected byte[] Call(BlockHeader header, Transaction transaction)
        {
            return CallCore(_transactionProcessor, header, transaction);
        }

        protected object[] Call(BlockHeader header, AbiFunctionDescription function, Address sender, params object[] arguments)
        {
            var transaction = GenerateTransaction(AbiEncoder.Encode(function.GetCallInfo(), arguments), sender);
            var result = Call(header, transaction);
            var objects = AbiEncoder.Decode(function.GetReturnInfo(), result);
            return objects;
        }
        
        protected byte[] CallCore(ITransactionProcessor transactionProcessor, BlockHeader header, Transaction transaction)
        {
            bool failure;
            
            CallOutputTracer tracer = new CallOutputTracer();
            
            try
            {
                transactionProcessor.Execute(transaction, header, tracer);
                failure = tracer.StatusCode != StatusCode.Success;
            }
            catch (Exception e)
            {
                throw new AuRaException($"System call returned an exception '{e.Message}' at block {header.Number}.", e);
            }
           
            if (failure)
            {
                throw new AuRaException($"System call returned error '{tracer.Error}' at block {header.Number}.");
            }
            else
            {
                return tracer.ReturnValue;
            }
        }

        protected bool TryCall(BlockHeader header, Transaction transaction, out byte[] result)
        {
            CallOutputTracer tracer = new CallOutputTracer();
            
            try
            {
                _transactionProcessor.Execute(transaction, header, tracer);
                result = tracer.ReturnValue;
                return tracer.StatusCode == StatusCode.Success;
            }
            catch (Exception)
            {
                result = null;
                return false;
            }
        }

        protected bool TryCall(BlockHeader header, AbiFunctionDescription function, Address sender, out object[] result, params object[] arguments)
        {
            var transaction = GenerateTransaction(AbiEncoder.Encode(function.GetCallInfo(), arguments), sender);
            if (TryCall(header, transaction, out var bytes))
            {
                result = AbiEncoder.Decode(function.GetReturnInfo(), bytes);
                return true;
            }

            result = null;
            return false;
        }
        
        protected void EnsureSystemAccount(IStateProvider stateProvider)
        {
            if (!stateProvider.AccountExists(Address.SystemUser))
            {
                stateProvider.CreateAccount(Address.SystemUser, UInt256.Zero);
                stateProvider.Commit(Homestead.Instance);
            }
        }

        protected internal ConstantContract GetConstant(IStateProvider stateProvider, IReadOnlyTransactionProcessorSource readOnlyReadOnlyTransactionProcessorSource) => 
            new ConstantContract(this, stateProvider, readOnlyReadOnlyTransactionProcessorSource);

        protected internal class ConstantContract
        {
            public IStateProvider StateProvider { get; }

            private readonly Contract _contract;
            private readonly IReadOnlyTransactionProcessorSource _readOnlyReadOnlyTransactionProcessorSource;

            public ConstantContract(
                Contract contract,
                IStateProvider stateProvider, 
                IReadOnlyTransactionProcessorSource readOnlyReadOnlyTransactionProcessorSource)
            {
                StateProvider = stateProvider ?? throw new ArgumentNullException(nameof(stateProvider));
                _contract = contract;
                _readOnlyReadOnlyTransactionProcessorSource = readOnlyReadOnlyTransactionProcessorSource ?? throw new ArgumentNullException(nameof(readOnlyReadOnlyTransactionProcessorSource));
            }
        
            public byte[] Call(BlockHeader header, Transaction transaction)
            {
                using var readOnlyTransactionProcessor = _readOnlyReadOnlyTransactionProcessorSource.Get(StateProvider.StateRoot);
                return _contract.CallCore(readOnlyTransactionProcessor, header, transaction);
            }

            public object[] Call(BlockHeader header, AbiFunctionDescription function, Address sender, params object[] arguments)
            {
                var transaction = _contract.GenerateTransaction(_contract.AbiEncoder.Encode(function.GetCallInfo(), arguments), sender);
                var result = Call(header, transaction);
                var objects = _contract.AbiEncoder.Decode(function.GetReturnInfo(), result);
                return objects;
            }

            public T Call<T>(BlockHeader header, AbiFunctionDescription function, Address sender,params object[] arguments)
            {
                return (T) Call(header, function, sender, arguments)[0];
            }
        }
    }
}