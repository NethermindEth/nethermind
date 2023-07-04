// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Synchronization.FastBlocks
{
    public class BodiesSyncBatch : FastBlocksBatch
    {
        public BodiesSyncBatch(BlockInfo[] infos)
        {
            Infos = infos;
        }

        public BlockInfo?[] Infos { get; }
        public BlockBody?[]? Response { get; set; }
    }
}
