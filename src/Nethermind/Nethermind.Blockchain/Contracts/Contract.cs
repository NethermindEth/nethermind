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
using Nethermind.Blockchain.Contracts.Json;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;

namespace Nethermind.Blockchain.Contracts
{
    /// <summary>
    /// Base class for contracts that will be interacted by the node engine.
    /// </summary>
    /// <remarks>
    /// This class is intended to be inherited and concrete contract class should provide contract specific methods to be able for the node to use the contract. 
    /// 
    /// There are 3 main ways a node can interact with contract:
    /// 1. It can <see cref="GenerateTransaction{T}(string,Nethermind.Core.Address,object[])"/> that will be added to a block.
    /// 2. It can <see cref="CallableContract.Call(Nethermind.Core.BlockHeader,string,Nethermind.Core.Address,object[])"/> contract and modify current state of execution.
    /// 3. It can <see cref="ConstantContract.Call{T}"/> constant contract. This by design doesn't modify current state. It is designed as read-only operation that will allow the node to make decisions how it should operate.
    /// </remarks>
    public abstract partial class Contract
    {
        /// <summary>
        /// Default gas limit of transactions generated from contract. 
        /// </summary>
        public const long DefaultContractGasLimit = 1_600_000L;

        protected IAbiEncoder AbiEncoder { get; }
        public AbiDefinition AbiDefinition { get; }
        public Address ContractAddress { get; }

        /// <summary>
        /// Creates contract
        /// </summary>
        /// <param name="abiEncoder">Binary interface encoder/decoder.</param>
        /// <param name="contractAddress">Address where contract is deployed.</param>
        /// <param name="abiDefinition">Binary definition of contract.</param>
        protected Contract(IAbiEncoder abiEncoder, Address contractAddress, AbiDefinition abiDefinition = null)
        {
            AbiEncoder = abiEncoder ?? throw new ArgumentNullException(nameof(abiEncoder));
            ContractAddress = contractAddress ?? throw new ArgumentNullException(nameof(contractAddress));
            AbiDefinition = abiDefinition ?? new AbiDefinitionParser().Parse(GetType());
        }
        
        private Transaction GenerateTransaction<T>(byte[] transactionData, Address sender, long gasLimit = DefaultContractGasLimit) where T : Transaction, new()
        {
            var transaction = new T()
            {
                Value = UInt256.Zero,
                Data = transactionData,
                To = ContractAddress,
                SenderAddress = sender ?? Address.SystemUser,
                GasLimit = gasLimit,
                GasPrice = UInt256.Zero,
            };
                
            transaction.Hash = transaction.CalculateHash();

            return transaction;
        }

        /// <summary>
        /// Generates transaction.
        /// That transaction can be added to a produced block or broadcasted - if <see cref="GeneratedTransaction"/> is used as <see cref="T"/>.
        /// That transaction can be used in <see cref="CallableContract.Call(Nethermind.Core.BlockHeader,Nethermind.Core.Transaction)"/> if <see cref="SystemTransaction"/> is used as <see cref="T"/>.
        /// </summary>
        /// <param name="functionName">Function in contract that is called by the transaction.</param>
        /// <param name="sender">Sender of the transaction - caller of the function.</param>
        /// <param name="arguments">Arguments to the function.</param>
        /// <typeparam name="T">Type of <see cref="Transaction"/>.</typeparam>
        /// <returns>Transaction.</returns>
        protected Transaction GenerateTransaction<T>(string functionName, Address sender, params object[] arguments) where T : Transaction, new()
            => GenerateTransaction<T>(functionName, sender, DefaultContractGasLimit, arguments);
        
        /// <summary>
        /// Generates transaction.
        /// That transaction can be added to a produced block or broadcasted - if <see cref="GeneratedTransaction"/> is used as <see cref="T"/>.
        /// That transaction can be used in <see cref="CallableContract.Call(Nethermind.Core.BlockHeader,Nethermind.Core.Transaction)"/> if <see cref="SystemTransaction"/> is used as <see cref="T"/>.
        /// </summary>
        /// <param name="functionName">Function in contract that is called by the transaction.</param>
        /// <param name="sender">Sender of the transaction - caller of the function.</param>
        /// <param name="gasLimit">Gas limit for generated transaction.</param>
        /// <param name="arguments">Arguments to the function.</param>
        /// <typeparam name="T">Type of <see cref="Transaction"/>.</typeparam>
        /// <returns>Transaction.</returns>
        protected Transaction GenerateTransaction<T>(string functionName, Address sender, long gasLimit, params object[] arguments) where T : Transaction, new()
            => GenerateTransaction<T>(AbiEncoder.Encode(AbiDefinition.GetFunction(functionName).GetCallInfo(), arguments), sender, gasLimit);
        
        /// <summary>
        /// Helper method that actually does the actual call to <see cref="ITransactionProcessor"/>.
        /// </summary>
        /// <param name="transactionProcessor">Actual transaction processor to be called upon.</param>
        /// <param name="header">Header in which context the call is done.</param>
        /// <param name="transaction">Transaction to be executed.</param>
        /// <param name="callAndRestore">Is it restore call.</param>
        /// <returns>Bytes with result.</returns>
        /// <exception cref="AbiException">Thrown when there is an exception during execution or <see cref="CallOutputTracer.StatusCode"/> is <see cref="StatusCode.Failure"/>.</exception>
        protected static byte[] CallCore(ITransactionProcessor transactionProcessor, BlockHeader header, Transaction transaction, bool callAndRestore = false)
        {
            bool failure;
            
            CallOutputTracer tracer = new CallOutputTracer();
            
            try
            {
                if (callAndRestore)
                {
                    transactionProcessor.CallAndRestore(transaction, header, tracer);
                }
                else
                {
                    transactionProcessor.Execute(transaction, header, tracer);
                }
                
                failure = tracer.StatusCode != StatusCode.Success;
            }
            catch (Exception e)
            {
                throw new AbiException($"System call returned an exception '{e.Message}' at block {header.Number}.", e);
            }
           
            if (failure)
            {
                throw new AbiException($"System call returned error '{tracer.Error}' at block {header.Number}.");
            }
            else
            {
                return tracer.ReturnValue;
            }
        }
        
        protected Keccak GetEventHash(string eventName)
        {
            return AbiDefinition.Events[eventName].GetHash();
        }
        
        protected object[] DecodeReturnData(string functionName, byte[] data)
        {
            return AbiEncoder.Decode(AbiDefinition.GetFunction(functionName).GetReturnInfo(), data);
        }
    }
}
