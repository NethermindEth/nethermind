// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Synchronization.FastBlocks
{
    public class BodiesSyncBatch(BlockInfo[] infos) : FastBlocksBatch
    {
        public BlockInfo?[] Infos { get; } = infos;
        public OwnedBlockBodies? Response { get; set; }

        public override void Dispose()
        {
            base.Dispose();
            Response?.Dispose();
        }
    }
}
