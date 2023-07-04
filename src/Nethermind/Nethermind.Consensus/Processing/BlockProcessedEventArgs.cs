// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.Consensus.Processing
{
    public class BlockProcessedEventArgs : EventArgs
    {
        public Block Block { get; }
        public TxReceipt[] TxReceipts { get; }

        public BlockProcessedEventArgs(Block block, TxReceipt[] txReceipts)
        {
            Block = block;
            TxReceipts = txReceipts;
        }
    }
}
