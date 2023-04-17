// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.Blockchain.Receipts
{
    public class ReceiptsEventArgs : EventArgs
    {
        public TxReceipt[] TxReceipts { get; }
        public BlockHeader BlockHeader { get; }
        public bool WasRemoved { get; }

        public ReceiptsEventArgs(BlockHeader blockHeader, TxReceipt[] txReceipts, bool wasRemoved = false)
        {
            BlockHeader = blockHeader;
            TxReceipts = txReceipts;
            WasRemoved = wasRemoved;
        }
    }
}
