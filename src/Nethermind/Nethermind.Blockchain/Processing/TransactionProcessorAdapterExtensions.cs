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
// 

using Nethermind.Core;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.State;

namespace Nethermind.Blockchain.Processing
{
    internal static class TransactionProcessorAdapterExtensions
    {
        public static void ProcessTransaction(this ITransactionProcessorAdapter transactionProcessor, 
            Block block, 
            Transaction currentTx, 
            BlockReceiptsTracer receiptsTracer, 
            ProcessingOptions processingOptions,
            IStateProvider stateProvider)
        {
            if ((processingOptions & ProcessingOptions.DoNotVerifyNonce) != 0)
            {
                currentTx.Nonce = stateProvider.GetNonce(currentTx.SenderAddress);
            }

            receiptsTracer.StartNewTxTrace(currentTx);
            transactionProcessor.Execute(currentTx, block.Header, receiptsTracer);
            receiptsTracer.EndTxTrace();
        }
    }
}
