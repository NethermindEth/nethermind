// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.Consensus.Processing
{
    public class BlockProcessedEventArgs(Block block, TxReceipt[] txReceipts) : EventArgs
    {
        public Block Block { get; } = block;
        public TxReceipt[] TxReceipts { get; } = txReceipts;
    }
}
