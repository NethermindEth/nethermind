// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.Processing
{
    public partial class BlockProcessor
    {
        public interface IBlockProductionTransactionsExecutor : IBlockProcessor.IBlockTransactionsExecutor
        {
            public event EventHandler<AddingTxEventArgs> AddingTransaction;

            /// <summary>
            /// Reports that a block built by this executor ended <paramref name="excessItems"/> over the EIP-7928 block
            /// access list item limit and was rejected, so later builds on the same parent can leave more room for the
            /// end-of-block steps.
            /// </summary>
            void ReportItemLimitExceeded(long excessItems) { }
        }

        public class AddingTxEventArgs(int index, Transaction transaction, Block block, IReadOnlyCollection<Transaction> transactionsInBlock) : TxEventArgs(index, transaction)
        {
            public Block Block { get; } = block;
            public IReadOnlyCollection<Transaction> TransactionsInBlock { get; } = transactionsInBlock;
            internal AggregationInput? LeanProofInput { get; set; }
            internal IReadOnlyList<FrameDependency> LeanDependencies { get; set; } = [];
            public TxAction Action { get; private set; } = TxAction.Add;
            public string Reason { get; private set; } = string.Empty;

            public AddingTxEventArgs Set(TxAction action, string reason)
            {
                Action = action;
                Reason = reason;
                return this;
            }
        }
    }
}
