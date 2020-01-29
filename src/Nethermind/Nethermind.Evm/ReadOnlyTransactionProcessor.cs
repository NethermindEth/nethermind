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

using Nethermind.Core;
using Nethermind.Evm.Tracing;
using Nethermind.Store;

namespace Nethermind.Evm
{
    public class ReadOnlyTransactionProcessor : IReadOnlyTransactionProcessor
    {
        private readonly ITransactionProcessor _transactionProcessor;
        private readonly IStateProvider _stateProvider;

        public ReadOnlyTransactionProcessor(ITransactionProcessor transactionProcessor, IStateProvider stateProvider)
        {
            _transactionProcessor = transactionProcessor;
            _stateProvider = stateProvider;
        }
        
        public void Execute(Transaction transaction, BlockHeader block, ITxTracer txTracer)
        {
            _transactionProcessor.Execute(transaction, block, txTracer);
        }

        public void CallAndRestore(Transaction transaction, BlockHeader block, ITxTracer txTracer)
        {
            _transactionProcessor.CallAndRestore(transaction, block, txTracer);
        }

        public void Dispose()
        {
            _stateProvider.Reset();
        }
    }
}