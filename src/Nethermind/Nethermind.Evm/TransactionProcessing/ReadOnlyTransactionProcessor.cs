//  Copyright (c) 2021 Demerzel Solutions Limited
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
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.Tracing;
using Nethermind.State;

namespace Nethermind.Evm.TransactionProcessing
{
    public class ReadOnlyTransactionProcessor : IReadOnlyTransactionProcessor
    {
        private readonly ITransactionProcessor _transactionProcessor;
        private readonly IStateProvider _stateProvider;
        private readonly IStorageProvider _storageProvider;
        private readonly Keccak _stateBefore;

        public ReadOnlyTransactionProcessor(ITransactionProcessor transactionProcessor, IStateProvider stateProvider, IStorageProvider storageProvider, Keccak startState)
        {
            _transactionProcessor = transactionProcessor ?? throw new ArgumentNullException(nameof(transactionProcessor));
            _stateProvider = stateProvider ?? throw new ArgumentNullException(nameof(stateProvider));
            _storageProvider = storageProvider ?? throw new ArgumentNullException(nameof(storageProvider));
            _stateBefore = _stateProvider.StateRoot;
            _stateProvider.StateRoot = startState ?? throw new ArgumentException(nameof(startState));
        }
        
        public void Execute(Transaction transaction, BlockHeader block, ITxTracer txTracer)
        {
            _transactionProcessor.Execute(transaction, block, txTracer);
        }

        public void CallAndRestore(Transaction transaction, BlockHeader block, ITxTracer txTracer)
        {
            _transactionProcessor.CallAndRestore(transaction, block, txTracer);
        }

        public void BuildUp(Transaction transaction, BlockHeader block, ITxTracer txTracer)
        {
            _transactionProcessor.BuildUp(transaction, block, txTracer);
        }

        public bool IsContractDeployed(Address address) => _stateProvider.IsContract(address);

        public void Dispose()
        {
            _stateProvider.StateRoot = _stateBefore;
            _stateProvider.Reset();
            _storageProvider.Reset();
        }
    }
}
