using Cortex.SimpleSerialize;
using Nethermind.BeaconNode.Containers;
using Nethermind.Core2.Crypto;

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
