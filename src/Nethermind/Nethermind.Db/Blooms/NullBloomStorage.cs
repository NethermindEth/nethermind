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
        public ulong MinBlockNumber { get; } = ulong.MaxValue;
        public ulong MaxBlockNumber { get; } = 0;
        public ulong MigratedBlockNumber { get; } = 0;

        public void Store(IReadOnlyList<(ulong BlockNumber, Bloom Bloom)> blooms) { }
        public void Store(ulong blockNumber, Bloom bloom) { }

        public void Migrate(IEnumerable<BlockHeader> blockHeaders) { }

        public IBloomEnumeration GetBlooms(ulong fromBlock, ulong toBlock) => new NullBloomEnumerator();

        public bool ContainsRange(in ulong fromBlockNumber, in ulong toBlockNumber) => false;

        public IEnumerable<Average> Averages { get; } = Array.Empty<Average>();


        private class NullBloomEnumerator : IBloomEnumeration
        {
            public IEnumerator<Core.Bloom> GetEnumerator() => Enumerable.Empty<Core.Bloom>().GetEnumerator();

            public bool TryGetBlockNumber(out ulong blockNumber)
            {
                blockNumber = default;
                return false;
            }

            public (ulong FromBlock, ulong ToBlock) CurrentIndices { get; } = (0, 0);

            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }

        public void Dispose() { }
    }
}
