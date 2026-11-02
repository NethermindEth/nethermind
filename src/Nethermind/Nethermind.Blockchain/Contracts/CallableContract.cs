// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Specs.Forks;

namespace Nethermind.Blockchain.Contracts
{
    /// <summary>
    /// Creates contract
    /// </summary>
    /// <param name="transactionProcessor">Transaction processor on which all calls should be run on.</param>
    /// <param name="contractAddress">Address where contract is deployed.</param>
    public abstract class CallableContract(ITransactionProcessor transactionProcessor, Address contractAddress) : Contract(contractAddress)
    {
        private readonly ITransactionProcessor _transactionProcessor = transactionProcessor ?? throw new ArgumentNullException(nameof(transactionProcessor));
        public const ulong UnlimitedGas = ulong.MaxValue;

        /// <summary>
        /// Gas limit used for generated system transactions.
        /// </summary>
        public const ulong SystemTransactionGasLimit = 30_000_000UL;

        /// <summary>
        /// Calls the function in contract, state modification is allowed.
        /// </summary>
        /// <param name="header">Header in which context the call is done.</param>
        /// <param name="function">Function to call.</param>
        /// <param name="sender">Sender of the transaction - caller of the function.</param>
        /// <param name="arguments">Arguments to the function.</param>
        /// <returns>Decoded return values of <paramref name="function"/>.</returns>
        protected TResult Call<TArgs, TResult>(BlockHeader header, ContractFunction<TArgs, TResult> function, Address sender, TArgs arguments) =>
            Call(header, function, sender, DefaultContractGasLimit, arguments);

        /// <summary>
        /// Calls the function in contract, state modification is allowed.
        /// </summary>
        /// <param name="header">Header in which context the call is done.</param>
        /// <param name="function">Function to call.</param>
        /// <param name="sender">Sender of the transaction - caller of the function.</param>
        /// <param name="gasLimit">Gas limit for generated transaction.</param>
        /// <param name="arguments">Arguments to the function.</param>
        /// <returns>Decoded return values of <paramref name="function"/>.</returns>
        protected TResult Call<TArgs, TResult>(BlockHeader header, ContractFunction<TArgs, TResult> function, Address sender, ulong gasLimit, TArgs arguments)
        {
            Transaction transaction = GenerateTransaction<SystemTransaction>(EncodeCallData(function, arguments), sender, gasLimit, header);
            byte[] result = CallCore(_transactionProcessor, header, function.Signature.Name, transaction);
            return DecodeReturnData(function, result);
        }

        private bool TryCall(BlockHeader header, Transaction transaction, out byte[] result)
        {
            CallOutputTracer tracer = new();

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

        /// <summary>
        /// Same as <see cref="Call{TArgs,TResult}(BlockHeader,ContractFunction{TArgs,TResult},Address,ulong,TArgs)"/> but returns false instead of throwing when the call fails.
        /// </summary>
        /// <param name="header">Header in which context the call is done.</param>
        /// <param name="function">Function to call.</param>
        /// <param name="sender">Sender of the transaction - caller of the function.</param>
        /// <param name="gasLimit">Gas limit for generated transaction.</param>
        /// <param name="arguments">Arguments to the function.</param>
        /// <param name="result">Decoded return values of <paramref name="function"/>.</param>
        /// <returns>true if function was <see cref="StatusCode.Success"/> otherwise false.</returns>
        protected bool TryCall<TArgs, TResult>(BlockHeader header, ContractFunction<TArgs, TResult> function, Address sender, ulong gasLimit, TArgs arguments, out TResult result)
        {
            Transaction transaction = GenerateTransaction<SystemTransaction>(EncodeCallData(function, arguments), sender, gasLimit, header);
            if (TryCall(header, transaction, out byte[] bytes))
            {
                result = DecodeReturnData(function, bytes);
                return true;
            }

            result = default;
            return false;
        }

        /// <summary>
        /// Creates <see cref="Address.SystemUser"/> account if its not in current state.
        /// </summary>
        /// <param name="stateProvider">State provider.</param>
        protected static void EnsureSystemAccount(IWorldState stateProvider)
        {
            if (!stateProvider.AccountExists(Address.SystemUser))
            {
                stateProvider.CreateAccount(Address.SystemUser, UInt256.Zero);
                stateProvider.Commit(Homestead.Instance);
            }
        }
    }
}
