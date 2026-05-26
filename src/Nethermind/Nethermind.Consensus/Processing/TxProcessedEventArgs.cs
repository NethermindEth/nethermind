// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Consensus.Processing
{
    public class TxProcessedEventArgs(int index, Transaction transaction, BlockHeader blockHeader, TxReceipt txReceipt) : TxEventArgs(index, transaction)
    {
        public TxReceipt TxReceipt { get; } = txReceipt;

        public BlockHeader BlockHeader { get; } = blockHeader;
    }
}
