// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;

namespace Nethermind.Db.Blooms
{
    public class NullBloomStorage : IBloomStorage
    {
        private NullBloomStorage()
        {
        }

        public static NullBloomStorage Instance { get; } = new();
        public long MinBlockNumber { get; } = long.MaxValue;
        public long MaxBlockNumber { get; } = 0;
        public long MigratedBlockNumber { get; } = -1;

        public void Store(long blockNumber, Core.Bloom bloom) { }
        public void Migrate(IEnumerable<BlockHeader> blockHeaders) { }

        public IBloomEnumeration GetBlooms(long fromBlock, long toBlock) => new NullBloomEnumerator();

        public bool ContainsRange(in long fromBlockNumber, in long toBlockNumber) => false;

        public IEnumerable<Average> Averages { get; } = Array.Empty<Average>();


        private class NullBloomEnumerator : IBloomEnumeration
        {
            public IEnumerator<Core.Bloom> GetEnumerator() => Enumerable.Empty<Core.Bloom>().GetEnumerator();

            public bool TryGetBlockNumber(out long blockNumber)
            {
                blockNumber = default;
                return false;
            }

            public (long FromBlock, long ToBlock) CurrentIndices { get; } = (0, 0);

            IEnumerator IEnumerable.GetEnumerator()
            {
                return GetEnumerator();
            }
        }

        public void Dispose() { }
    }
}
