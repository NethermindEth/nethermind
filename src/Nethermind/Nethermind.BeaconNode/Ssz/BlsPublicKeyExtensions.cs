using Cortex.Containers;
using Cortex.SimpleSerialize;

namespace Cortex.BeaconNode.Ssz
{
    public static class BlsPublicKeyExtensions
    {
        public static SszElement ToSszBasicVector(this BlsPublicKey item)
        {
            return new SszBasicVector(item.AsSpan());
        }
    }
}
