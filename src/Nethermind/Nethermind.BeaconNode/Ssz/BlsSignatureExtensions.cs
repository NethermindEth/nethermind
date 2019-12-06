using Cortex.Containers;
using Cortex.SimpleSerialize;

namespace Cortex.BeaconNode.Ssz
{
    public static class BlsSignatureExtensions
    {
        public static SszElement ToSszBasicVector(this BlsSignature item)
        {
            return new SszBasicVector(item.AsSpan());
        }
    }
}
