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
using Nethermind.Core;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Specs.Forks;
using Nethermind.State;

namespace Nethermind.Consensus.AuRa.Contracts
{
    public class ConstantContract : Contract
    {
        protected IStateProvider StateProvider { get; }
        
        private readonly IReadOnlyTransactionProcessorSource _readOnlyReadOnlyTransactionProcessorSource;

        protected ConstantContract(
            ITransactionProcessor transactionProcessor, 
            IAbiEncoder abiEncoder, 
            Address contractAddress, 
            IStateProvider stateProvider, 
            IReadOnlyTransactionProcessorSource readOnlyReadOnlyTransactionProcessorSource) 
            : base(transactionProcessor, abiEncoder, contractAddress)
        {
            StateProvider = stateProvider ?? throw new ArgumentNullException(nameof(stateProvider));
            _readOnlyReadOnlyTransactionProcessorSource = readOnlyReadOnlyTransactionProcessorSource ?? throw new ArgumentNullException(nameof(readOnlyReadOnlyTransactionProcessorSource));
        }
        
        protected byte[] CallConstant(BlockHeader header, Transaction transaction)
        {
            return CallCore(_readOnlyReadOnlyTransactionProcessorSource.Get(StateProvider.StateRoot), header, transaction);
        }

        protected object[] CallConstant(BlockHeader header, AbiFunctionDescription function, params object[] arguments)
        {
            var transaction = GenerateTransaction(AbiEncoder.Encode(function.GetCallInfo(), arguments));
            var result = CallConstant(header, transaction);
            var objects = AbiEncoder.Decode(function.GetReturnInfo(), result);
            return objects;
        }

        protected T CallConstant<T>(BlockHeader header, AbiFunctionDescription function, params object[] arguments)
        {
            return (T) CallConstant(header, function, arguments)[0];
        }
    }
}