// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Abi;
using Nethermind.Blockchain.Contracts.Json;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;

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
    /// 3. It can <see cref="IConstantContract.Call{T}(Nethermind.Core.BlockHeader,string,Nethermind.Core.Address,object[])"/> constant contract. This by design doesn't modify current state. It is designed as read-only operation that will allow the node to make decisions how it should operate.
    /// </remarks>
    public abstract partial class Contract
    {
        /// <summary>
        /// Default gas limit of transactions generated from contract. 
        /// </summary>
        public const long DefaultContractGasLimit = 1_600_000L;

        protected IAbiEncoder AbiEncoder { get; }
        public AbiDefinition AbiDefinition { get; }
        public Address? ContractAddress { get; protected set; }

        /// <summary>
        /// Creates contract
        /// </summary>
        /// <param name="abiEncoder">Binary interface encoder/decoder.</param>
        /// <param name="contractAddress">Address where contract is deployed.</param>
        /// <param name="abiDefinition">Binary definition of contract.</param>
        protected Contract(IAbiEncoder? abiEncoder = null, Address? contractAddress = null, AbiDefinition? abiDefinition = null)
        {
            AbiEncoder = abiEncoder ?? Abi.AbiEncoder.Instance;
            ContractAddress = contractAddress;
            AbiDefinition = abiDefinition ?? new AbiDefinitionParser().Parse(GetType());
        }

        protected virtual Transaction GenerateTransaction<T>(Address? contractAddress, byte[] transactionData, Address sender, long gasLimit = DefaultContractGasLimit, BlockHeader header = null)
            where T : Transaction, new() =>
            GenerateTransaction<T>(contractAddress, transactionData, sender, gasLimit);

        protected Transaction GenerateTransaction<T>(Address? contractAddress, byte[] transactionData, Address? sender, long gasLimit = DefaultContractGasLimit) where T : Transaction, new()
        {
            var transaction = new T()
            {
                Value = UInt256.Zero,
                Data = transactionData,
                To = (contractAddress ?? ContractAddress) ?? throw new ArgumentNullException(nameof(contractAddress)),
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
        /// That transaction can be used in <see cref="CallableContract.Call(Nethermind.Core.BlockHeader,string,Nethermind.Core.Transaction)"/> if <see cref="SystemTransaction"/> is used as <see cref="T"/>.
        /// </summary>
        /// <param name="contractAddress">Dynamic contract address, optional</param>
        /// <param name="functionName">Function in contract that is called by the transaction.</param>
        /// <param name="sender">Sender of the transaction - caller of the function.</param>
        /// <param name="arguments">Arguments to the function.</param>
        /// <typeparam name="T">Type of <see cref="Transaction"/>.</typeparam>
        /// <returns>Transaction.</returns>
        protected Transaction GenerateTransaction<T>(Address? contractAddress, string functionName, Address sender, params object[] arguments) where T : Transaction, new()
            => GenerateTransaction<T>(contractAddress, functionName, sender, DefaultContractGasLimit, arguments);

        /// <summary>
        /// Generates transaction.
        /// That transaction can be added to a produced block or broadcasted - if <see cref="GeneratedTransaction"/> is used as <see cref="T"/>.
        /// That transaction can be used in <see cref="CallableContract.Call(Nethermind.Core.BlockHeader,string,Nethermind.Core.Transaction)"/> if <see cref="SystemTransaction"/> is used as <see cref="T"/>.
        /// </summary>
        /// <param name="contractAddress">Dynamic contract address, optional</param>
        /// <param name="functionName">Function in contract that is called by the transaction.</param>
        /// <param name="sender">Sender of the transaction - caller of the function.</param>
        /// <param name="gasLimit">Gas limit for generated transaction.</param>
        /// <param name="header">Header</param>
        /// <param name="arguments">Arguments to the function.</param>
        /// <typeparam name="T">Type of <see cref="Transaction"/>.</typeparam>
        /// <returns>Transaction.</returns>
        protected Transaction GenerateTransaction<T>(Address? contractAddress, string functionName, Address sender, long gasLimit, BlockHeader header, params object[] arguments) where T : Transaction, new()
            => GenerateTransaction<T>(contractAddress, AbiEncoder.Encode(AbiDefinition.GetFunction(functionName).GetCallInfo(), arguments), sender, gasLimit, header);

        /// <summary>
        /// Generates transaction.
        /// That transaction can be added to a produced block or broadcasted - if <see cref="GeneratedTransaction"/> is used as <see cref="T"/>.
        /// That transaction can be used in <see cref="CallableContract.Call(Nethermind.Core.BlockHeader,string,Nethermind.Core.Transaction)"/> if <see cref="SystemTransaction"/> is used as <see cref="T"/>.
        /// </summary>
        /// <param name="contractAddress">Dynamic contract address, optional</param>
        /// <param name="functionName">Function in contract that is called by the transaction.</param>
        /// <param name="sender">Sender of the transaction - caller of the function.</param>
        /// <param name="gasLimit">Gas limit for generated transaction.</param>
        /// <param name="arguments">Arguments to the function.</param>
        /// <typeparam name="T">Type of <see cref="Transaction"/>.</typeparam>
        /// <returns>Transaction.</returns>
        protected Transaction GenerateTransaction<T>(Address? contractAddress, string functionName, Address sender, long gasLimit, params object[] arguments) where T : Transaction, new()
            => GenerateTransaction<T>(contractAddress, AbiEncoder.Encode(AbiDefinition.GetFunction(functionName).GetCallInfo(), arguments), sender, gasLimit);

        /// <summary>
        /// Generates transaction.
        /// That transaction can be added to a produced block or broadcasted - if <see cref="GeneratedTransaction"/> is used as <see cref="T"/>.
        /// That transaction can be used in <see cref="CallableContract.Call(Nethermind.Core.BlockHeader,string,Nethermind.Core.Transaction)"/> if <see cref="SystemTransaction"/> is used as <see cref="T"/>.
        /// </summary>
        /// <param name="functionName">Function in contract that is called by the transaction.</param>
        /// <param name="sender">Sender of the transaction - caller of the function.</param>
        /// <param name="arguments">Arguments to the function.</param>
        /// <typeparam name="T">Type of <see cref="Transaction"/>.</typeparam>
        /// <returns>Transaction.</returns>
        protected Transaction GenerateTransaction<T>(string functionName, Address sender, params object[] arguments) where T : Transaction, new()
            => GenerateTransaction<T>(ContractAddress, functionName, sender, DefaultContractGasLimit, arguments);

        /// <summary>
        /// Generates transaction.
        /// That transaction can be added to a produced block or broadcasted - if <see cref="GeneratedTransaction"/> is used as <see cref="T"/>.
        /// That transaction can be used in <see cref="CallableContract.Call(Nethermind.Core.BlockHeader,string,Nethermind.Core.Transaction)"/> if <see cref="SystemTransaction"/> is used as <see cref="T"/>.
        /// </summary>
        /// <param name="functionName">Function in contract that is called by the transaction.</param>
        /// <param name="sender">Sender of the transaction - caller of the function.</param>
        /// <param name="gasLimit">Gas limit for generated transaction.</param>
        /// <param name="header">Header</param>
        /// <param name="arguments">Arguments to the function.</param>
        /// <typeparam name="T">Type of <see cref="Transaction"/>.</typeparam>
        /// <returns>Transaction.</returns>
        protected Transaction GenerateTransaction<T>(string functionName, Address sender, long gasLimit, BlockHeader header, params object[] arguments) where T : Transaction, new()
            => GenerateTransaction<T>(ContractAddress, AbiEncoder.Encode(AbiDefinition.GetFunction(functionName).GetCallInfo(), arguments), sender, gasLimit, header);

        /// <summary>
        /// Generates transaction.
        /// That transaction can be added to a produced block or broadcasted - if <see cref="GeneratedTransaction"/> is used as <see cref="T"/>.
        /// That transaction can be used in <see cref="CallableContract.Call(Nethermind.Core.BlockHeader,string,Nethermind.Core.Transaction)"/> if <see cref="SystemTransaction"/> is used as <see cref="T"/>.
        /// </summary>
        /// <param name="functionName">Function in contract that is called by the transaction.</param>
        /// <param name="sender">Sender of the transaction - caller of the function.</param>
        /// <param name="gasLimit">Gas limit for generated transaction.</param>
        /// <param name="arguments">Arguments to the function.</param>
        /// <typeparam name="T">Type of <see cref="Transaction"/>.</typeparam>
        /// <returns>Transaction.</returns>
        protected Transaction GenerateTransaction<T>(string functionName, Address sender, long gasLimit, params object[] arguments) where T : Transaction, new()
            => GenerateTransaction<T>(ContractAddress, AbiEncoder.Encode(AbiDefinition.GetFunction(functionName).GetCallInfo(), arguments), sender, gasLimit);

        /// <summary>
        /// Helper method that actually does the actual call to <see cref="ITransactionProcessor"/>.
        /// </summary>
        /// <param name="transactionProcessor">Actual transaction processor to be called upon.</param>
        /// <param name="header">Header in which context the call is done.</param>
        /// <param name="functionName">Function name.</param>
        /// <param name="transaction">Transaction to be executed.</param>
        /// <param name="callAndRestore">Is it restore call.</param>
        /// <returns>Bytes with result.</returns>
        /// <exception cref="AbiException">Thrown when there is an exception during execution or <see cref="CallOutputTracer.StatusCode"/> is <see cref="StatusCode.Failure"/>.</exception>
        protected byte[] CallCore(ITransactionProcessor transactionProcessor, BlockHeader header, string functionName, Transaction transaction, bool callAndRestore = false)
        {
            bool failure;

            CallOutputTracer tracer = new();

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
                throw new AbiException($"System call to {AbiDefinition.Name}.{functionName} returned an exception '{e.Message}' at block {header.Number}.", e);
            }

            if (failure)
            {
                throw new AbiException($"System call to {AbiDefinition.Name}.{functionName} returned error '{tracer.Error}' at block {header.Number}.");
            }
            else
            {
                return tracer.ReturnValue;
            }
        }
        protected object[] DecodeReturnData(string functionName, byte[] data)
        {
            AbiEncodingInfo abiEncodingInfo = AbiDefinition.GetFunction(functionName).GetReturnInfo();
            return DecodeData(abiEncodingInfo, data);
        }

        protected object[] DecodeData(AbiEncodingInfo abiEncodingInfo, byte[] data)
        {
            try
            {
                return AbiEncoder.Decode(abiEncodingInfo, data);
            }
            catch (Exception e)
            {
                throw new AbiException($"Cannot decode return data for function {abiEncodingInfo.Signature} for contract {ContractAddress}.", e);
            }
        }

        protected LogEntry GetSearchLogEntry(string eventName, byte[] data = null, params Keccak[] topics)
        {
            Keccak[] eventNameTopic = { AbiDefinition.GetEvent(eventName).GetHash() };
            topics = topics.Length == 0 ? eventNameTopic : eventNameTopic.Concat(topics).ToArray();
            return new LogEntry(ContractAddress, data ?? Array.Empty<byte>(), topics);
        }

        protected LogEntry GetSearchLogEntry(string eventName, params Keccak[] topics) => GetSearchLogEntry(eventName, null, topics);
    }
}
