using Cortex.SimpleSerialize;
using Nethermind.BeaconNode.Containers;
using Nethermind.Core2.Types;

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
