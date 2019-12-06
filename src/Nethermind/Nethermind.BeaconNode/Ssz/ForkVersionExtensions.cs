using Cortex.SimpleSerialize;
using Nethermind.BeaconNode.Containers;

namespace Nethermind.BeaconNode.Ssz
{
    public static class ForkVersionExtensions
    {
        public static SszElement ToSszBasicVector(this ForkVersion item)
        {
            return new SszBasicVector(item.AsSpan());
        }
    }
}
