// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db.LogIndex;

namespace Nethermind.Db.Test.LogIndex;

public static class LogIndexStorageTestExtensions
{
    extension(ILogIndexStorage storage)
    {
        public List<int> GetBlockNumbersFor(Address address, int from, int to)
        {
            var result = new List<int>();
            using IEnumerator<int> enumerator = storage.GetEnumerator(address, from, to);

            while (enumerator.MoveNext())
                result.Add(enumerator.Current);

            return result;
        }

        public List<int> GetBlockNumbersFor(int index, Hash256 topic, int from, int to)
        {
            var result = new List<int>();
            using IEnumerator<int> enumerator = storage.GetEnumerator(index, topic, from, to);

            while (enumerator.MoveNext())
                result.Add(enumerator.Current);

            return result;
        }

        public Task AddReceiptsAsync(IReadOnlyList<BlockReceipts> batch, bool isBackwardSync, LogIndexUpdateStats? stats = null)
        {
            LogIndexAggregate aggregate = storage.Aggregate(batch, isBackwardSync, stats);
            return storage.AddReceiptsAsync(aggregate, stats);
        }
    }
}
