using System.Collections.Generic;
using Cortex.Containers;
using Cortex.SimpleSerialize;

namespace Cortex.BeaconNode.Ssz
{
    public static class CheckpointExtensions
    {
        public static SszContainer ToSszContainer(this Checkpoint item)
        {
            return new SszContainer(GetValues(item));
        }

        private static IEnumerable<SszElement> GetValues(Checkpoint item)
        {
            yield return item.Epoch.ToSszBasicElement();
            yield return item.Root.ToSszBasicVector();
        }
    }
}
