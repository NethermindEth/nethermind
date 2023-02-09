// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Cortex.SimpleSerialize;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;

namespace Nethermind.Core2.Cryptography.Ssz
{
    public static class HistoricalBatchExtensions
    {
        public static Root HashTreeRoot(this HistoricalBatch item)
        {
            var tree = new SszTree(item.ToSszContainer());
            return new Root(tree.HashTreeRoot());
        }

        public static SszContainer ToSszContainer(this HistoricalBatch item)
        {
            return new SszContainer(GetValues(item));
        }

        private static IEnumerable<SszElement> GetValues(HistoricalBatch item)
        {
            yield return item.BlockRoots.ToSszVector();
            yield return item.StateRoots.ToSszVector();
        }
    }
}
