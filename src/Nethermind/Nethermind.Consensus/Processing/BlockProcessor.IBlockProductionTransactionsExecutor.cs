// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.Consensus.Processing
{
    public partial class BlockProcessor
    {
        public interface IBlockProductionTransactionsExecutor : IBlockProcessor.IBlockTransactionsExecutor
        {
            public event EventHandler<AddingTxEventArgs> AddingTransaction;
        }

        public class AddingTxEventArgs : TxEventArgs
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
