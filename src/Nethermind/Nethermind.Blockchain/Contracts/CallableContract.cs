﻿//  Copyright (c) 2018 Demerzel Solutions Limited
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
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Specs.Forks;
using Nethermind.State;

namespace Nethermind.Blockchain.Contracts
{
    public abstract class CallableContract : Contract
    {
        private readonly ITransactionProcessor _transactionProcessor;

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

        private byte[] Call(BlockHeader header, Transaction transaction) => CallCore(_transactionProcessor, header, transaction);

        /// <summary>
        /// Calls the function in contract, state modification is allowed.
        /// </summary>
        /// <param name="header">Header in which context the call is done.</param>
        /// <param name="functionName"></param>
        /// <param name="sender">Sender of the transaction - caller of the function.</param>
        /// <param name="arguments">Arguments to the function.</param>
        /// <returns>Deserialized return value of the <see cref="functionName"/> based on its definition.</returns>
        protected object[] Call(BlockHeader header, string functionName, Address sender, params object[] arguments)
        {
            var function = AbiDefinition.GetFunction(functionName);
            var transaction = GenerateTransaction<SystemTransaction>(functionName, sender, arguments);
            var result = Call(header, transaction);
            var objects = AbiEncoder.Decode(function.GetReturnInfo(), result);
            return objects;
        }

        private bool TryCall(BlockHeader header, Transaction transaction, out byte[] result)
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

        /// <summary>
        /// Same as <see cref="Call(Nethermind.Core.BlockHeader,AbiFunctionDescription,Address,object[])"/> but returns false instead of throwing <see cref="AbiException"/>.
        /// </summary>
        /// <param name="header">Header in which context the call is done.</param>
        /// <param name="functionName"></param>
        /// <param name="sender">Sender of the transaction - caller of the function.</param>
        /// <param name="result">Deserialized return value of the <see cref="functionName"/> based on its definition.</param>
        /// <param name="arguments">Arguments to the function.</param>
        /// <returns>true if function was <see cref="StatusCode.Success"/> otherwise false.</returns>
        protected bool TryCall(BlockHeader header, string functionName, Address sender, out object[] result, params object[] arguments)
        {
            var function = AbiDefinition.GetFunction(functionName);
            var transaction = GenerateTransaction<SystemTransaction>(functionName, sender, arguments);
            if (TryCall(header, transaction, out var bytes))
            {
                result = AbiEncoder.Decode(function.GetReturnInfo(), bytes);
                return true;
            }

            result = null;
            return false;
        }
        
        /// <summary>
        /// Creates <see cref="Address.SystemUser"/> account if its not in current state.
        /// </summary>
        /// <param name="stateProvider">State provider.</param>
        protected void EnsureSystemAccount(IStateProvider stateProvider)
        {
            if (!stateProvider.AccountExists(Address.SystemUser))
            {
                stateProvider.CreateAccount(Address.SystemUser, UInt256.Zero);
                stateProvider.Commit(Homestead.Instance);
            }
        }
    }
}
