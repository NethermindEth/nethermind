// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Blockchain.Filters
{
    public class BlockFilter : FilterBase
    {
        public long StartBlockNumber { get; set; }

        public BlockFilter(int id, long startBlockNumber) : base(id)
        {
            StartBlockNumber = startBlockNumber;
        }
    }
}
