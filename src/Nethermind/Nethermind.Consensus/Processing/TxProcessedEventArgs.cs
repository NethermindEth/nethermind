// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Consensus.Processing
{
    public class TxProcessedEventArgs : TxEventArgs
    {
        public TxReceipt TxReceipt { get; }

        public BlockHeader BlockHeader { get; }

        public TxProcessedEventArgs(int index, Transaction transaction, BlockHeader blockHeader, TxReceipt txReceipt) : base(index, transaction)
        {
            TxReceipt = txReceipt;
            BlockHeader = blockHeader;
        }
    }
}
