// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;

namespace Nethermind.Synchronization.FastBlocks
{
    public class ReceiptsSyncBatch(BlockInfo?[] infos) : FastBlocksBatch
    {
        public BlockInfo?[] Infos { get; } = infos;
        public IOwnedReadOnlyList<TxReceipt[]?>? Response { get; set; }

        public override void Dispose()
        {
            base.Dispose();
            Response?.Dispose();
        }
    }
}
