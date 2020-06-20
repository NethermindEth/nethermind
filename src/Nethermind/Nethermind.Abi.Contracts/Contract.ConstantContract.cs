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
using Nethermind.Core.Crypto;
using Nethermind.Evm;
using Nethermind.State;

namespace Nethermind.Consensus.AuRa.Contracts
{
    public partial class Contract
    {
        /// <summary>
        /// Gets constant version of the contract. Allowing to call contract methods without state modification.
        /// </summary>
        /// <param name="readOnlyReadOnlyTransactionProcessorSource">Source of readonly <see cref="ITransactionProcessor"/> to call transactions.</param>
        /// <returns>Constant version of the contract.</returns>
        protected ConstantContract GetConstant(IReadOnlyTransactionProcessorSource readOnlyReadOnlyTransactionProcessorSource) =>
            new ConstantContract(this, readOnlyReadOnlyTransactionProcessorSource);

        /// <summary>
        /// Constant version of the contract. Allows to call contract methods without state modification.
        /// </summary>
        protected class ConstantContract
        {
            private readonly Contract _contract;
            private readonly IReadOnlyTransactionProcessorSource _readOnlyReadOnlyTransactionProcessorSource;

            public ConstantContract(Contract contract, IReadOnlyTransactionProcessorSource readOnlyReadOnlyTransactionProcessorSource)
            {
                _contract = contract;
                _readOnlyReadOnlyTransactionProcessorSource = readOnlyReadOnlyTransactionProcessorSource ?? throw new ArgumentNullException(nameof(readOnlyReadOnlyTransactionProcessorSource));
            }

            private byte[] Call(BlockHeader parentHeader, Transaction transaction)
            {
                using var readOnlyTransactionProcessor = _readOnlyReadOnlyTransactionProcessorSource.Get(GetState(parentHeader));
                return CallCore(readOnlyTransactionProcessor, parentHeader, transaction, true);
            }

            private object[] Call(BlockHeader parentHeader, AbiFunctionDescription function, Address sender, params object[] arguments)
            {
                var result = CallRaw(parentHeader, function, sender, arguments);
                var objects = _contract.AbiEncoder.Decode(function.GetReturnInfo(), result);
                return objects;
            }

            /// <summary>
            /// 
            /// </summary>
            /// <param name="parentHeader"></param>
            /// <param name="function"></param>
            /// <param name="sender"></param>
            /// <param name="arguments"></param>
            /// <typeparam name="T"></typeparam>
            /// <returns></returns>
            public T Call<T>(BlockHeader parentHeader, AbiFunctionDescription function, Address sender, params object[] arguments)
            {
                return (T) Call(parentHeader, function, sender, arguments)[0];
            }
            
            /// <summary>
            /// 
            /// </summary>
            /// <param name="parentHeader"></param>
            /// <param name="function"></param>
            /// <param name="sender"></param>
            /// <param name="arguments"></param>
            /// <typeparam name="T1"></typeparam>
            /// <typeparam name="T2"></typeparam>
            /// <returns></returns>
            public (T1, T2) Call<T1, T2>(BlockHeader parentHeader, AbiFunctionDescription function, Address sender,params object[] arguments)
            {
                var objects = Call(parentHeader, function, sender, arguments);
                return ((T1) objects[0], (T2) objects[1]);
            }
            
            /// <summary>
            /// 
            /// </summary>
            /// <param name="parentHeader"></param>
            /// <param name="function"></param>
            /// <param name="sender"></param>
            /// <param name="arguments"></param>
            /// <returns></returns>
            public byte[] CallRaw(BlockHeader parentHeader, AbiFunctionDescription function, Address sender, params object[] arguments)
            {
                var transaction = _contract.GenerateTransaction<SystemTransaction>(function, sender, arguments);
                var result = Call(parentHeader, transaction);
                return result;
            }
            
            private Keccak GetState(BlockHeader parentHeader) => parentHeader?.StateRoot ?? Keccak.EmptyTreeHash;
        }
    }
}