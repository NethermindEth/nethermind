using Cortex.Containers;
using Cortex.SimpleSerialize;

namespace Cortex.BeaconNode.Ssz
{
    public static class ShardExtensions
    {
        public static SszElement ToSszBasicElement(this Shard item)
        {
            return new SszBasicElement((ulong)item);
        }
    }
}
