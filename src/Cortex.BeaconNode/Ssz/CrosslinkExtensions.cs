using System.Collections.Generic;
using Cortex.Containers;
using Cortex.SimpleSerialize;

namespace Cortex.BeaconNode.Ssz
{
    public static class CrosslinkExtensions
    {
        public static SszContainer ToSszContainer(this Crosslink item)
        {
            return new SszContainer(GetValues(item));
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
