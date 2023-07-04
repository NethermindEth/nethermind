// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Synchronization.FastBlocks
{
    public class ReceiptsSyncBatch : FastBlocksBatch
    {
        public BlockInfo?[] Infos { get; }
        public TxReceipt[]?[]? Response { get; set; }

        public ReceiptsSyncBatch(BlockInfo?[] infos)
        {
            Infos = infos;
        }
    }
}
