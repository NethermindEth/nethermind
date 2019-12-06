using Cortex.SimpleSerialize;
using Nethermind.BeaconNode.Containers;

namespace Nethermind.BeaconNode.Ssz
{
    public static class ShardExtensions
    {
        public static SszElement ToSszBasicElement(this Shard item)
        {
            return new SszBasicElement((ulong)item);
        }
    }
}
