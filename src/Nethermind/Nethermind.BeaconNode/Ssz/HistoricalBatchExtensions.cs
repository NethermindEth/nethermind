﻿using System.Collections.Generic;
using Cortex.Containers;
using Cortex.SimpleSerialize;

namespace Cortex.BeaconNode.Ssz
{
    public static class HistoricalBatchExtensions
    {
        public static Hash32 HashTreeRoot(this HistoricalBatch item)
        {
            var tree = new SszTree(item.ToSszContainer());
            return new Hash32(tree.HashTreeRoot());
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
