// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Abi;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Evm;
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
    /// 1. It can <see cref="GenerateTransaction{T}(byte[],Nethermind.Core.Address,ulong,Nethermind.Core.BlockHeader)"/> that will be added to a block.
    /// 2. It can call a <see cref="CallableContract"/> and modify current state of execution.
    /// 3. It can call a constant contract, see <see cref="GetConstant"/>. This by design doesn't modify current state. It is designed as read-only operation that will allow the node to make decisions how it should operate.
    /// </remarks>
    /// <param name="contractAddress">Address where contract is deployed.</param>
    public abstract partial class Contract(Address? contractAddress = null)
    {
        /// <summary>
        /// Default gas limit of transactions generated from contract.
        /// </summary>
        public const ulong DefaultContractGasLimit = 1_600_000UL;

        public Address? ContractAddress { get; protected set; } = contractAddress;

        protected virtual Transaction GenerateTransaction<T>(Address? contractAddress, byte[] transactionData, Address sender, ulong gasLimit = DefaultContractGasLimit, BlockHeader header = null)
            where T : Transaction, new() =>
            GenerateTransaction<T>(contractAddress, transactionData, sender, gasLimit);

        protected Transaction GenerateTransaction<T>(Address? contractAddress, byte[] transactionData, Address? sender, ulong gasLimit = DefaultContractGasLimit) where T : Transaction, new()
        {
            T transaction = new()
            {
                Value = 0,
                Data = transactionData,
                To = (contractAddress ?? ContractAddress) ?? throw new ArgumentNullException(nameof(contractAddress)),
                SenderAddress = sender ?? Address.SystemUser,
                GasLimit = gasLimit,
                GasPrice = 0,
            };

            transaction.Hash = transaction.CalculateHash();

            return transaction;
        }

        /// <summary>
        /// Generates transaction to <see cref="ContractAddress"/>.
        /// That transaction can be added to a produced block or broadcasted - if <see cref="GeneratedTransaction"/> is used as <see cref="T"/>.
        /// That transaction can be called if <see cref="SystemTransaction"/> is used as <see cref="T"/>.
        /// </summary>
        /// <param name="transactionData">Call data, e.g. encoded with <see cref="AbiCodec"/>.</param>
        /// <param name="sender">Sender of the transaction - caller of the function.</param>
        /// <param name="gasLimit">Gas limit for generated transaction.</param>
        /// <param name="header">Header</param>
        /// <typeparam name="T">Type of <see cref="Transaction"/>.</typeparam>
        /// <returns>Transaction.</returns>
        protected Transaction GenerateTransaction<T>(byte[] transactionData, Address sender, ulong gasLimit = DefaultContractGasLimit, BlockHeader header = null) where T : Transaction, new()
            => GenerateTransaction<T>(ContractAddress, transactionData, sender, gasLimit, header);

        /// <summary>
        /// Helper method that actually does the actual call to <see cref="ITransactionProcessor"/>.
        /// </summary>
        /// <param name="transactionProcessor">Actual transaction processor to be called upon.</param>
        /// <param name="header">Header in which context the call is done.</param>
        /// <param name="functionName">Function name.</param>
        /// <param name="transaction">Transaction to be executed.</param>
        /// <param name="callAndRestore">Is it restore call.</param>
        /// <returns>Bytes with result.</returns>
        /// <exception cref="ContractException">Thrown when there is an exception during execution.</exception>
        /// <exception cref="ContractCallFailedException">Thrown when <see cref="StatusCode"/> is <see cref="StatusCode.Failure"/>.</exception>
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
                throw new ContractException($"System call to {GetType().Name}.{functionName} returned an exception '{e.Message}' at block {header.Number}.", e);
            }

            if (failure)
            {
                throw new ContractCallFailedException($"System call to {GetType().Name}.{functionName} returned error '{tracer.Error}' at block {header.Number}.");
            }
            else
            {
                return tracer.ReturnValue;
            }
        }

        /// <exception cref="ContractException"><paramref name="data"/> is not the canonical encoding of the return values of <paramref name="function"/>.</exception>
        protected TResult DecodeReturnData<TArgs, TResult>(ContractFunction<TArgs, TResult> function, byte[] data)
        {
            try
            {
                return AbiCodec.Decode(function.Returns, data);
            }
            catch (AbiException e)
            {
                throw new ContractException($"Cannot decode return data for function {function} for contract {ContractAddress}.", e);
            }
        }

        /// <exception cref="ContractException"><paramref name="data"/> is not the canonical encoding under <paramref name="signature"/>.</exception>
        protected T DecodeData<T>(AbiSignature<T> signature, byte[] data)
        {
            try
            {
                return AbiCodec.Decode(signature, data);
            }
            catch (AbiException e)
            {
                throw new ContractException($"Cannot decode {signature} data for contract {ContractAddress}.", e);
            }
        }

        protected LogEntry GetSearchLogEntry(Hash256 eventHash, params Hash256[] topics)
        {
            Hash256[] eventNameTopic = { eventHash };
            topics = topics.Length == 0 ? eventNameTopic : eventNameTopic.Concat(topics).ToArray();
            return new LogEntry(ContractAddress, [], topics);
        }
    }
}
