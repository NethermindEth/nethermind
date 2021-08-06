//  Copyright (c) 2021 Demerzel Solutions Limited
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
// 

using System;
using Nethermind.Abi;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Blockchain.Contracts
{
    public partial class Contract
    {
        private const long DefaultConstantContractGasLimit = 50_000_000L;
        
        /// <summary>
        /// Gets constant version of the contract. Allowing to call contract methods without state modification.
        /// </summary>
        /// <param name="readOnlyTxProcessorSource">Source of readonly <see cref="ITransactionProcessor"/> to call transactions.</param>
        /// <returns>Constant version of the contract.</returns>
        protected IConstantContract GetConstant(IReadOnlyTxProcessorSource readOnlyTxProcessorSource) =>
            new ConstantContract(this, readOnlyTxProcessorSource);
        
        protected interface IConstantContract
        {
            public object[] Call(CallInfo callInfo);
            
            public T Call<T>(CallInfo callInfo) => (T)Call(callInfo)[0];

            public (T1, T2) Call<T1, T2>(CallInfo callInfo)
            {
                var objects = Call(callInfo);
                return ((T1) objects[0], (T2) objects[1]);
            }
            
            public T Call<T>(BlockHeader parentHeader, string functionName, Address sender, params object[] arguments) => 
                Call<T>(new CallInfo(parentHeader, functionName, sender, arguments));

            public (T1, T2) Call<T1, T2>(BlockHeader parentHeader, string functionName, Address sender, params object[] arguments) => 
                Call<T1,T2>(new CallInfo(parentHeader, functionName, sender, arguments));
            
            public T Call<T>(BlockHeader parentHeader, Address contractAddress, string functionName, Address sender, params object[] arguments) => 
                Call<T>(new CallInfo(parentHeader, functionName, sender, arguments) {ContractAddress = contractAddress});

            public (T1, T2) Call<T1, T2>(BlockHeader parentHeader, Address contractAddress, string functionName, Address sender, params object[] arguments) =>
                Call<T1, T2>(new CallInfo(parentHeader, functionName, sender, arguments) {ContractAddress = contractAddress});
        }
        
        protected abstract class ConstantContractBase : IConstantContract
        {
            protected readonly Contract _contract;

            protected ConstantContractBase(Contract contract)
            {
                _contract = contract;
            }
            
            protected Transaction GenerateTransaction(CallInfo callInfo) => 
                _contract.GenerateTransaction<SystemTransaction>(callInfo.ContractAddress, callInfo.FunctionName, callInfo.Sender, DefaultConstantContractGasLimit, callInfo.ParentHeader, callInfo.Arguments);
        
            protected byte[] CallCore(CallInfo callInfo, IReadOnlyTransactionProcessor readOnlyTransactionProcessor, Transaction transaction) => 
                _contract.CallCore(readOnlyTransactionProcessor, callInfo.ParentHeader, callInfo.FunctionName, transaction, true);
            
            protected object[] DecodeReturnData(string functionName, byte[] data) => _contract.DecodeReturnData(functionName, data);

            public abstract object[] Call(CallInfo callInfo);
        }

        /// <summary>
        /// Constant version of the contract. Allows to call contract methods without state modification.
        /// </summary>
        protected class ConstantContract : ConstantContractBase
        {
            private readonly IReadOnlyTxProcessorSource _readOnlyTxProcessorSource;

            public ConstantContract(Contract contract, IReadOnlyTxProcessorSource readOnlyTxProcessorSource)
                : base(contract)
            {
                _readOnlyTxProcessorSource = readOnlyTxProcessorSource ?? throw new ArgumentNullException(nameof(readOnlyTxProcessorSource));
            }
            
            public override object[] Call(CallInfo callInfo)
            {
                Keccak GetState(BlockHeader parentHeader) => parentHeader?.StateRoot ?? Keccak.EmptyTreeHash;
                
                lock (_readOnlyTxProcessorSource)
                {
                    using var readOnlyTransactionProcessor = _readOnlyTxProcessorSource.Build(GetState(callInfo.ParentHeader));
                    return CallRaw(callInfo, readOnlyTransactionProcessor);
                }
            }

            protected virtual object[] CallRaw(CallInfo callInfo, IReadOnlyTransactionProcessor readOnlyTransactionProcessor)
            {
                var transaction = GenerateTransaction(callInfo);
                if (_contract.ContractAddress is not null && readOnlyTransactionProcessor.IsContractDeployed(_contract.ContractAddress))
                {                    
                    var result = CallCore(callInfo, readOnlyTransactionProcessor, transaction);
                    return callInfo.Result = _contract.DecodeReturnData(callInfo.FunctionName, result);
                }
                else if (callInfo.MissingContractResult != null)
                {
                    return callInfo.MissingContractResult;
                }
                else
                {
                    throw new AbiException($"Missing contract on address {_contract.ContractAddress} when calling function {callInfo.FunctionName}.");
                }
            }
        }
        
        public class CallInfo
        {
            public BlockHeader ParentHeader { get; }
            public string FunctionName { get; }
            public Address Sender { get; }
            public object[] Arguments { get; }
            public object[]? Result { get; set; }
            public object[]? MissingContractResult { get; set; }
            public Address? ContractAddress { get; set; }
                
            public CallInfo(BlockHeader parentHeader, string functionName, Address sender, params object[] arguments)
            {
                ParentHeader = parentHeader;
                FunctionName = functionName;
                Sender = sender;
                Arguments = arguments;
            }

            public bool IsDefaultResult => ReferenceEquals(Result, MissingContractResult);
        }
    }
}
