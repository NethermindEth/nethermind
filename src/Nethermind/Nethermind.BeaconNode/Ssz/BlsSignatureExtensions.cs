using Cortex.SimpleSerialize;
using Nethermind.BeaconNode.Containers;
using Nethermind.Core2.Crypto;

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
