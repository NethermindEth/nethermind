using Cortex.SimpleSerialize;
using Nethermind.BeaconNode.Containers;

namespace Nethermind.BeaconNode.Ssz
{
    public static class BlsSignatureExtensions
    {
        public static SszElement ToSszBasicVector(this BlsSignature item)
        {
            return new SszBasicVector(item.AsSpan());
        }
    }
}
