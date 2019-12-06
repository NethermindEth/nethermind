using Cortex.SimpleSerialize;
using Nethermind.BeaconNode.Containers;

namespace Nethermind.BeaconNode.Ssz
{
    public static class Bytes32Extensions
    {
        public static SszElement ToSszBasicVector(this Bytes32 item)
        {
            return new SszBasicVector(item.AsSpan());
        }
    }
}
