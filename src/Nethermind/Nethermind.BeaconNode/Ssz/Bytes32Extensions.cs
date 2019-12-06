using Cortex.SimpleSerialize;
using Nethermind.BeaconNode.Containers;
using Nethermind.Core2.Types;

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
