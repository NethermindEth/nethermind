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
            private string _reason = string.Empty;
            private DeferredSkipReason _pendingReason;
            private ulong _reasonFirst;
            private ulong _reasonSecond;

            public Block Block { get; private set; } = block;
            public IReadOnlyCollection<Transaction> TransactionsInBlock { get; private set; } = transactionsInBlock;
            public TxAction Action { get; private set; } = TxAction.Add;

            /// <remarks>A reason set from numbers is formatted on first read, so a skip nobody logs builds no string.</remarks>
            public string Reason
            {
                get
                {
                    if (_pendingReason != DeferredSkipReason.None)
                    {
                        _reason = FormatReason(_pendingReason, _reasonFirst, _reasonSecond);
                        _pendingReason = DeferredSkipReason.None;
                    }

                    return _reason;
                }
            }

            public AddingTxEventArgs Set(TxAction action, string reason)
            {
                Action = action;
                _reason = reason;
                _pendingReason = DeferredSkipReason.None;
                return this;
            }

            /// <summary>Sets a reason that is formatted from <paramref name="first"/> and <paramref name="second"/> only when read.</summary>
            internal AddingTxEventArgs Set(TxAction action, DeferredSkipReason reason, ulong first, ulong second = 0)
            {
                Action = action;
                _pendingReason = reason;
                _reasonFirst = first;
                _reasonSecond = second;
                return this;
            }

            /// <summary>Points these args at another candidate, as if newly constructed for it.</summary>
            internal AddingTxEventArgs Reset(int index, Transaction transaction, Block block, IReadOnlyCollection<Transaction> transactionsInBlock)
            {
                Index = index;
                Transaction = transaction;
                Block = block;
                TransactionsInBlock = transactionsInBlock;
                Action = TxAction.Add;
                _reason = string.Empty;
                _pendingReason = DeferredSkipReason.None;
                return this;
            }

            private static string FormatReason(DeferredSkipReason reason, ulong first, ulong second) => reason switch
            {
                DeferredSkipReason.ExecutionGas => $"Not enough execution gas in block, gas limit {first} > {second}",
                DeferredSkipReason.StateGas => $"Not enough state gas in block, gas limit {first} > {second}",
                DeferredSkipReason.Nonce => $"Invalid nonce - expected {first}",
                _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null)
            };
        }

        /// <summary>The skip reasons the picker formats from numbers, so only when they are read.</summary>
        internal enum DeferredSkipReason
        {
            None,
            ExecutionGas,
            StateGas,
            Nonce
        }
    }
}
