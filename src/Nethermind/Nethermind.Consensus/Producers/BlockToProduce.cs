// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Nethermind.Core;

//TODO: Redo clique block producer
[assembly: InternalsVisibleTo("Nethermind.Consensus.Clique")]
[assembly: InternalsVisibleTo("Nethermind.Blockchain.Test")]

namespace Nethermind.Consensus.Producers
{
    [DebuggerDisplay("{Hash} ({Number})")]
    public class BlockToProduce : Block
    {
        private IEnumerable<MempoolTransaction>? _transactions;

        /// <summary>
        /// The candidate transactions before processing and the included ones after it, each with the blob sidecar it
        /// travels with, so a produced payload can be served with its blobs bundle.
        /// </summary>
        public new IEnumerable<MempoolTransaction> Transactions
        {
            get => _transactions ?? Array.ConvertAll(base.Transactions, static tx => (MempoolTransaction)tx);
            set
            {
                _transactions = value;
                if (_transactions is MempoolTransaction[] transactionsArray)
                {
                    base.Transactions = Array.ConvertAll(transactionsArray, static tx => tx.Transaction);
                }
            }
        }

        public BlockToProduce(BlockHeader header) : base(
        header,
        new(
            null,
            null,
            header.WithdrawalsRoot is null ? null : [])
        )
        { }

        public BlockToProduce(BlockHeader blockHeader,
            IEnumerable<MempoolTransaction> transactions,
            IEnumerable<BlockHeader> uncles,
            IEnumerable<Withdrawal>? withdrawals = null)
            : base(blockHeader, Array.Empty<Transaction>(), uncles, withdrawals) => Transactions = transactions;

        public long TxByteLength { get; internal set; }

        public override Block WithReplacedHeader(BlockHeader newHeader) => new BlockToProduce(newHeader, Transactions, Uncles, Withdrawals);
    }
}
