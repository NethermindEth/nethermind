// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Abi;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.State;

namespace Nethermind.Blockchain.Contracts
{
    public abstract class CallableContract : Contract
    {
        private readonly ITransactionProcessor _transactionProcessor;
        public const long UnlimitedGas = long.MaxValue;

        /// <summary>
        /// Creates contract
        /// </summary>
        /// <param name="transactionProcessor">Transaction processor on which all <see cref="Call(Nethermind.Core.BlockHeader,Nethermind.Core.Transaction)"/> should be run on.</param>
        /// <param name="abiEncoder">Binary interface encoder/decoder.</param>
        /// <param name="contractAddress">Address where contract is deployed.</param>
        protected CallableContract(ITransactionProcessor transactionProcessor, IAbiEncoder abiEncoder, Address contractAddress) : base(abiEncoder, contractAddress)
        {
            _transactionProcessor = transactionProcessor ?? throw new ArgumentNullException(nameof(transactionProcessor));
        }

        private byte[] Call(BlockHeader header, string functionName, Transaction transaction, IBlockTracer? blockTracer) => CallCore(_transactionProcessor, header, functionName, transaction, blockTracer);

        /// <summary>
        /// Calls the function in contract, state modification is allowed.
        /// </summary>
        /// <param name="header">Header in which context the call is done.</param>
        /// <param name="functionName"></param>
        /// <param name="sender">Sender of the transaction - caller of the function.</param>
        /// <param name="blockTracer">BlockTrace to trace the transaction execution</param>
        /// <param name="arguments">Arguments to the function.</param>
        /// <returns>Deserialized return value of the <see cref="functionName"/> based on its definition.</returns>
        protected object[] Call(BlockHeader header, string functionName, Address sender, IBlockTracer? blockTracer, params object[] arguments) =>
            Call(header, functionName, sender, DefaultContractGasLimit, blockTracer, arguments);

        /// <summary>
        /// Calls the function in contract, state modification is allowed.
        /// </summary>
        /// <param name="header">Header in which context the call is done.</param>
        /// <param name="functionName"></param>
        /// <param name="sender">Sender of the transaction - caller of the function.</param>
        /// <param name="gasLimit">Gas limit for generated transaction.</param>
        /// <param name="blockTracer">BlockTrace to trace the transaction execution</param>
        /// <param name="arguments">Arguments to the function.</param>
        /// <returns>Deserialized return value of the <see cref="functionName"/> based on its definition.</returns>
        protected object[] Call(BlockHeader header, string functionName, Address sender, long gasLimit, IBlockTracer? blockTracer, params object[] arguments)
        {
            var function = AbiDefinition.GetFunction(functionName);
            var transaction = GenerateTransaction<SystemTransaction>(functionName, sender, gasLimit, header, arguments);
            var result = Call(header, functionName, transaction, blockTracer);
            var objects = DecodeData(function.GetReturnInfo(), result);
            return objects;
        }

        private bool TryCall(BlockHeader header, Transaction transaction, IBlockTracer? blockTracer, out byte[] result)
        {
            CallOutputTracer outputTracer = new ();
            ITxTracer tracer;
            if (blockTracer is not null)
            {
                ITxTracer customTracer = blockTracer.StartNewTxTrace(transaction);
                tracer = new CompositeTxTracer(outputTracer, customTracer);
            }
            else
            {
                tracer = outputTracer;
            }

            try
            {
                _transactionProcessor.Execute(transaction, new BlockExecutionContext(header), tracer);
                blockTracer?.EndTxTrace();
                result = outputTracer.ReturnValue;
                return outputTracer.StatusCode == StatusCode.Success;
            }
            catch (Exception)
            {
                result = null;
                blockTracer?.EndTxTrace();
                return false;
            }
        }

        /// <summary>
        /// Same as <see cref="Call(Nethermind.Core.BlockHeader,AbiFunctionDescription,Address,object[])"/> but returns false instead of throwing <see cref="AbiException"/>.
        /// </summary>
        /// <param name="header">Header in which context the call is done.</param>
        /// <param name="functionName"></param>
        /// <param name="sender">Sender of the transaction - caller of the function.</param>
        /// <param name="blockTracer">BlockTrace to trace the transaction execution</param>
        /// <param name="result">Deserialized return value of the <see cref="functionName"/> based on its definition.</param>
        /// <param name="arguments">Arguments to the function.</param>
        /// <returns>true if function was <see cref="StatusCode.Success"/> otherwise false.</returns>
        protected bool TryCall(BlockHeader header, string functionName, Address sender, IBlockTracer? blockTracer, out object[] result, params object[] arguments) =>
            TryCall(header, functionName, sender, DefaultContractGasLimit, blockTracer, out result, arguments);

        /// <summary>
        /// Same as <see cref="Call(Nethermind.Core.BlockHeader,AbiFunctionDescription,Address,object[])"/> but returns false instead of throwing <see cref="AbiException"/>.
        /// </summary>
        /// <param name="header">Header in which context the call is done.</param>
        /// <param name="functionName"></param>
        /// <param name="sender">Sender of the transaction - caller of the function.</param>
        /// <param name="gasLimit">Gas limit for generated transaction.</param>
        /// <param name="blockTracer">BlockTrace to trace the transaction execution</param>
        /// <param name="result">Deserialized return value of the <see cref="functionName"/> based on its definition.</param>
        /// <param name="arguments">Arguments to the function.</param>
        /// <returns>true if function was <see cref="StatusCode.Success"/> otherwise false.</returns>
        protected bool TryCall(BlockHeader header, string functionName, Address sender, long gasLimit, IBlockTracer? blockTracer, out object[] result, params object[] arguments)
        {
            var function = AbiDefinition.GetFunction(functionName);
            var transaction = GenerateTransaction<SystemTransaction>(functionName, sender, gasLimit, header, arguments);
            if (TryCall(header, transaction, blockTracer, out var bytes))
            {
                result = DecodeData(function.GetReturnInfo(), bytes);
                return true;
            }

            result = null;
            return false;
        }

        /// <summary>
        /// Creates <see cref="Address.SystemUser"/> account if its not in current state.
        /// </summary>
        /// <param name="stateProvider">State provider.</param>
        protected void EnsureSystemAccount(IWorldState stateProvider)
        {
            if (!stateProvider.AccountExists(Address.SystemUser))
            {
                stateProvider.CreateAccount(Address.SystemUser, UInt256.Zero);
                stateProvider.Commit(Homestead.Instance);
            }
        }
    }
}
