// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Consensus.Processing
{
    /// <remarks>
    /// <see cref="TxReceipt"/> is the live receipt: while the block executes its <see cref="TxReceipt.GasUsedTotal"/> is the
    /// running block total; EIP-8116's per-transaction value is applied when the block is finalised, so events published after
    /// validation observe it.
    /// </remarks>
    public class TxProcessedEventArgs(int index, Transaction transaction, BlockHeader blockHeader, TxReceipt txReceipt) : TxEventArgs(index, transaction)
    {
        public TxReceipt TxReceipt { get; } = txReceipt;

        public BlockHeader BlockHeader { get; } = blockHeader;

        /// <summary>Gets the cumulative header gas used when the transaction completed.</summary>
        public ulong HeaderGasUsed { get; init; } = blockHeader.GasUsed;
    }
}
