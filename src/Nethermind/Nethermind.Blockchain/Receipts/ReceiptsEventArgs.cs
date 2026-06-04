// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.Blockchain.Receipts
{
    public class ReceiptsEventArgs(BlockHeader blockHeader, TxReceipt[] txReceipts, bool wasRemoved = false) : EventArgs
    {
        public TxReceipt[] TxReceipts { get; } = txReceipts;
        public BlockHeader BlockHeader { get; } = blockHeader;
        public bool WasRemoved { get; } = wasRemoved;
    }
}
