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

            /// <summary>
            /// Reports that a block built by this executor ended <paramref name="excessItems"/> over the EIP-7928 block
            /// access list item limit and was rejected, so later builds on the same parent can leave more room for the
            /// end-of-block steps.
            /// </summary>
            void ReportItemLimitExceeded(long excessItems) { }
        }

        public class AddingTxEventArgs(int index, Transaction transaction, Block block, IReadOnlyCollection<Transaction> transactionsInBlock) : TxEventArgs(index, transaction)
        {
            /// <summary>Stands in for a reason that a derived class formats only when <see cref="Reason"/> is read; compared by reference.</summary>
            private static readonly string DeferredReason = new('?', 1);

            private string _reason = string.Empty;

            public Block Block { get; } = block;
            public IReadOnlyCollection<Transaction> TransactionsInBlock { get; } = transactionsInBlock;
            public TxAction Action { get; private set; } = TxAction.Add;
            public string Reason => ReferenceEquals(_reason, DeferredReason) ? _reason = FormatDeferredReason() : _reason;

            /// <summary>Whether the transaction is skipped because it is invalid at this index, rather than by the builder's
            /// policy; an omission claim is made only for such a skip.</summary>
            public bool IsInvalidHere { get; private set; }

            public AddingTxEventArgs Set(TxAction action, string reason)
            {
                Action = action;
                _reason = reason;
                IsInvalidHere = false;
                return this;
            }

            /// <summary>Skips the transaction as invalid at this index.</summary>
            public AddingTxEventArgs SetInvalid(string reason)
            {
                Set(TxAction.Skip, reason);
                IsInvalidHere = true;
                return this;
            }

            /// <summary>Sets <paramref name="action"/> with a reason <see cref="FormatDeferredReason"/> builds on the first read of <see cref="Reason"/>.</summary>
            private protected void SetDeferred(TxAction action, bool invalidHere = false)
            {
                Set(action, DeferredReason);
                IsInvalidHere = invalidHere;
            }

            private protected virtual string FormatDeferredReason() => throw new InvalidOperationException("No deferred reason to format.");
        }
    }
}
