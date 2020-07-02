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
// 

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm;

namespace Nethermind.Blockchain.Contracts
{
    public partial class Contract
    {
        /// <summary>
        /// Gets constant version of the contract. Allowing to call contract methods without state modification.
        /// </summary>
        /// <param name="readOnlyTransactionProcessorSource">Source of readonly <see cref="ITransactionProcessor"/> to call transactions.</param>
        /// <returns>Constant version of the contract.</returns>
        protected ConstantContract GetConstant(IReadOnlyTransactionProcessorSource readOnlyTransactionProcessorSource) =>
            new ConstantContract(this, readOnlyTransactionProcessorSource);

        /// <summary>
        /// Constant version of the contract. Allows to call contract methods without state modification.
        /// </summary>
        public class ConstantContract
        {
            private readonly Contract _contract;
            private readonly IReadOnlyTransactionProcessorSource _readOnlyTransactionProcessorSource;
            public const long DefaultConstantContractGasLimit = 50_000_000L;

            public ConstantContract(Contract contract, IReadOnlyTransactionProcessorSource readOnlyTransactionProcessorSource)
            {
                _contract = contract;
                _readOnlyTransactionProcessorSource = readOnlyTransactionProcessorSource ?? throw new ArgumentNullException(nameof(readOnlyTransactionProcessorSource));
            }

            private byte[] Call(BlockHeader parentHeader, Transaction transaction)
            {
                using var readOnlyTransactionProcessor = _readOnlyTransactionProcessorSource.Get(GetState(parentHeader));
                return CallCore(readOnlyTransactionProcessor, parentHeader, transaction, true);
            }

            private object[] Call(BlockHeader parentHeader, string functionName, Address sender, params object[] arguments)
            {
                return CallRaw(parentHeader, functionName, sender, arguments); ;
            }

            /// <summary>
            /// 
            /// </summary>
            /// <param name="parentHeader"></param>
            /// <param name="functionName"></param>
            /// <param name="sender"></param>
            /// <param name="arguments"></param>
            /// <typeparam name="T"></typeparam>
            /// <returns></returns>
            public T Call<T>(BlockHeader parentHeader, string functionName, Address sender, params object[] arguments)
            {
                return (T) Call(parentHeader, functionName, sender, arguments)[0];
            }

            /// <summary>
            /// 
            /// </summary>
            /// <param name="parentHeader"></param>
            /// <param name="functionName"></param>
            /// <param name="sender"></param>
            /// <param name="arguments"></param>
            /// <typeparam name="T1"></typeparam>
            /// <typeparam name="T2"></typeparam>
            /// <returns></returns>
            public (T1, T2) Call<T1, T2>(BlockHeader parentHeader, string functionName, Address sender,params object[] arguments)
            {
                var objects = Call(parentHeader, functionName, sender, arguments);
                return ((T1) objects[0], (T2) objects[1]);
            }

            /// <summary>
            /// 
            /// </summary>
            /// <param name="parentHeader"></param>
            /// <param name="functionName"></param>
            /// <param name="sender"></param>
            /// <param name="arguments"></param>
            /// <returns></returns>
            public object[] CallRaw(BlockHeader parentHeader, string functionName, Address sender, params object[] arguments)
            {
                var transaction = _contract.GenerateTransaction<SystemTransaction>(functionName, sender, DefaultConstantContractGasLimit, arguments);
                var result = Call(parentHeader, transaction);
                var objects = _contract.DecodeReturnData(functionName, result);
                return objects;
            }
            
            private Keccak GetState(BlockHeader parentHeader) => parentHeader?.StateRoot ?? Keccak.EmptyTreeHash;
        }
    }
}
