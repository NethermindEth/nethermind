using System.Collections.Generic;
using System.Linq;
using Cortex.SimpleSerialize;
using Nethermind.BeaconNode.Containers;
using Nethermind.Core2.Types;

namespace Nethermind.BeaconNode.Ssz
{
    public static class CrosslinkExtensions
    {
        public static Hash32 HashTreeRoot(this Crosslink item)
        {
            var tree = new SszTree(item.ToSszContainer());
            return new Hash32(tree.HashTreeRoot());
        }

        public static SszContainer ToSszContainer(this Crosslink item)
        {
            return new SszContainer(GetValues(item));
        }

        public static SszVector ToSszVector(this IEnumerable<Crosslink> vector)
        {
            return new SszVector(vector.Select(x => x.ToSszContainer()));
        }

        private static IEnumerable<SszElement> GetValues(Crosslink item)
        {
            yield return item.Shard.ToSszBasicElement();
            yield return item.ParentRoot.ToSszBasicVector();
            // Crosslinking data
            yield return item.StartEpoch.ToSszBasicElement();
            yield return item.EndEpoch.ToSszBasicElement();
            yield return item.DataRoot.ToSszBasicVector();
        }
    }
}
