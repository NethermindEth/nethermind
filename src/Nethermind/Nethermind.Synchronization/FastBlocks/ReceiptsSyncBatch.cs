// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;

namespace Nethermind.Synchronization.FastBlocks
{
    public class ReceiptsSyncBatch : FastBlocksBatch
    {
        public BlockInfo?[] Infos { get; }
        public IDisposableReadOnlyList<TxReceipt[]?>? Response { get; set; }

        public ReceiptsSyncBatch(BlockInfo?[] infos)
        {
            Infos = infos;
        }
    }
}
