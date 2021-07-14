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

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.State;
using Nethermind.State.Proofs;
using Nethermind.TxPool;
using Nethermind.TxPool.Comparison;

namespace Nethermind.Blockchain.Processing
{
    public partial class BlockProcessor
    {
        protected interface IBlockProductionTransactionsExecutor : IBlockProcessor.IBlockTransactionsExecutor
        {
            public event EventHandler<AddingTxEventArgs> AddingTransaction;
        }

        protected class AddingTxEventArgs : TxEventArgs
        {
            public Block Block { get; }
            public IReadOnlyCollection<Transaction> TransactionsInBlock { get; }
            public TxAction Action { get; private set; } = TxAction.Add;
            public string Reason { get; private set; } = string.Empty;

            public AddingTxEventArgs Set(TxAction action, string reason)
            {
                Action = action;
                Reason = reason;
                return this;
            }

            public AddingTxEventArgs(int index, Transaction transaction, Block block, IReadOnlyCollection<Transaction> transactionsInBlock) 
                : base(index, transaction)
            {
                Block = block;
                TransactionsInBlock = transactionsInBlock;
            }
        }
    }
}
