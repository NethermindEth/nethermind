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
using Nethermind.Abi;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm;
using Nethermind.State;

namespace Nethermind.Consensus.AuRa.Contracts
{
    public partial class Contract
    {
        protected ConstantContract GetConstant(IReadOnlyTransactionProcessorSource readOnlyReadOnlyTransactionProcessorSource, IStateProvider stateProvider = null) =>
            stateProvider == null 
                ? new ConstantContract(this, readOnlyReadOnlyTransactionProcessorSource) 
                : new ConstantContractOnState(this, readOnlyReadOnlyTransactionProcessorSource, stateProvider);

        protected internal class ConstantContract
        {
            private readonly Contract _contract;
            private readonly IReadOnlyTransactionProcessorSource _readOnlyReadOnlyTransactionProcessorSource;

            public ConstantContract(Contract contract, IReadOnlyTransactionProcessorSource readOnlyReadOnlyTransactionProcessorSource)
            {
                _contract = contract;
                _readOnlyReadOnlyTransactionProcessorSource = readOnlyReadOnlyTransactionProcessorSource ?? throw new ArgumentNullException(nameof(readOnlyReadOnlyTransactionProcessorSource));
            }
        
            public byte[] Call(BlockHeader header, Transaction transaction)
            {
                using var readOnlyTransactionProcessor = _readOnlyReadOnlyTransactionProcessorSource.Get(GetState(header));
                return _contract.CallCore(readOnlyTransactionProcessor, header, transaction, true);
            }

            protected virtual Keccak GetState(BlockHeader header) => header.StateRoot;

            public object[] Call(BlockHeader header, AbiFunctionDescription function, Address sender, params object[] arguments)
            {
                var result = CallRaw(header, function, sender, arguments);
                var objects = _contract.AbiEncoder.Decode(function.GetReturnInfo(), result);
                return objects;
            }

            public T Call<T>(BlockHeader header, AbiFunctionDescription function, Address sender, params object[] arguments)
            {
                return (T) Call(header, function, sender, arguments)[0];
            }
            
            public (T1, T2) Call<T1, T2>(BlockHeader header, AbiFunctionDescription function, Address sender,params object[] arguments)
            {
                var objects = Call(header, function, sender, arguments);
                return ((T1) objects[0], (T2) objects[1]);
            }
            
            public byte[] CallRaw(BlockHeader header, AbiFunctionDescription function, Address sender, params object[] arguments)
            {
                var transaction = _contract.GenerateTransaction<SystemTransaction>(function, sender, arguments);
                var result = Call(header, transaction);
                return result;
            }
        }
        
        protected internal class ConstantContractOnState : ConstantContract
        {
            public IStateProvider StateProvider { get; }

            public ConstantContractOnState(Contract contract,
                IReadOnlyTransactionProcessorSource readOnlyReadOnlyTransactionProcessorSource,
                IStateProvider stateProvider) : base(contract, readOnlyReadOnlyTransactionProcessorSource)
            {
                StateProvider = stateProvider ?? throw new ArgumentNullException(nameof(stateProvider));
            }

            protected override Keccak GetState(BlockHeader header) => StateProvider.StateRoot;
        }
    }
}