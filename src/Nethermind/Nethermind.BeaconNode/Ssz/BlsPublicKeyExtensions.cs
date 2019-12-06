using Cortex.SimpleSerialize;
using Nethermind.BeaconNode.Containers;

namespace Nethermind.BeaconNode.Ssz
{
    public static class BlsPublicKeyExtensions
    {
        public static SszElement ToSszBasicVector(this BlsPublicKey item)
        {
            return new SszBasicVector(item.AsSpan());
        }
    }
}
