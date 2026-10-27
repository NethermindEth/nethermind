// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Abi;
using Nethermind.Core;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Blockchain.Contracts
{
    public partial class Contract
    {
        private const ulong DefaultConstantContractGasLimit = 50_000_000UL;

        /// <summary>
        /// Gets constant version of the contract. Allowing to call contract methods without state modification.
        /// </summary>
        /// <param name="readOnlyTxProcessorSource">Source of readonly <see cref="ITransactionProcessor"/> to call transactions.</param>
        /// <returns>Constant version of the contract.</returns>
        protected IConstantContract GetConstant(IReadOnlyTxProcessorSource readOnlyTxProcessorSource) =>
            new ConstantContract(this, readOnlyTxProcessorSource);

        protected interface IConstantContract
        {
            /// <summary>Calls <paramref name="function"/> without modifying state.</summary>
            /// <exception cref="ContractException">The call fails, its return data cannot be decoded, or there is no contract at the contract address.</exception>
            TResult Call<TArgs, TResult>(BlockHeader parentHeader, ContractFunction<TArgs, TResult> function, Address sender, TArgs arguments);

            /// <summary>Calls <paramref name="function"/> without modifying state, returning <paramref name="missingContractResult"/> when there is no contract at the contract address.</summary>
            /// <exception cref="ContractException">The call fails or its return data cannot be decoded.</exception>
            TResult Call<TArgs, TResult>(BlockHeader parentHeader, ContractFunction<TArgs, TResult> function, Address sender, TArgs arguments, TResult missingContractResult);
        }

        /// <summary>
        /// Constant version of the contract. Allows to call contract methods without state modification.
        /// </summary>
        protected class ConstantContract(Contract contract, IReadOnlyTxProcessorSource readOnlyTxProcessorSource) : IConstantContract
        {
            protected readonly Contract _contract = contract;
            protected readonly IReadOnlyTxProcessorSource _readOnlyTxProcessorSource = readOnlyTxProcessorSource ?? throw new ArgumentNullException(nameof(readOnlyTxProcessorSource));

            public TResult Call<TArgs, TResult>(BlockHeader parentHeader, ContractFunction<TArgs, TResult> function, Address sender, TArgs arguments)
            {
                lock (_readOnlyTxProcessorSource)
                {
                    using IReadOnlyTxProcessingScope scope = _readOnlyTxProcessorSource.Build(parentHeader);
                    return Call(scope, parentHeader, function, sender, arguments);
                }
            }

            public TResult Call<TArgs, TResult>(BlockHeader parentHeader, ContractFunction<TArgs, TResult> function, Address sender, TArgs arguments, TResult missingContractResult)
            {
                lock (_readOnlyTxProcessorSource)
                {
                    using IReadOnlyTxProcessingScope scope = _readOnlyTxProcessorSource.Build(parentHeader);
                    return TryCall(scope, parentHeader, function, sender, arguments, out TResult result) ? result : missingContractResult;
                }
            }

            protected TResult Call<TArgs, TResult>(IReadOnlyTxProcessingScope scope, BlockHeader parentHeader, ContractFunction<TArgs, TResult> function, Address sender, TArgs arguments) =>
                TryCall(scope, parentHeader, function, sender, arguments, out TResult result)
                    ? result
                    : throw new ContractException($"Missing contract on address {_contract.ContractAddress} when calling function {function}.");

            private bool TryCall<TArgs, TResult>(IReadOnlyTxProcessingScope scope, BlockHeader parentHeader, ContractFunction<TArgs, TResult> function, Address sender, TArgs arguments, out TResult result)
            {
                Transaction transaction = _contract.GenerateTransaction<SystemTransaction>(AbiCodec.Encode(function.Signature, arguments), sender, DefaultConstantContractGasLimit, parentHeader);
                if (_contract.ContractAddress is not null && scope.WorldState.IsContract(_contract.ContractAddress))
                {
                    byte[] output = _contract.CallCore(scope.TransactionProcessor, parentHeader, function.Signature.Name, transaction, true);
                    result = _contract.DecodeReturnData(function, output);
                    return true;
                }

                result = default;
                return false;
            }
        }
    }
}
