using Cortex.BeaconNode.Ssz;
using Cortex.Containers;

namespace Cortex.BeaconNode.Tests.Helpers
{
    public static class TestBlockHeader
    {
        public static void SignBlockHeader(BeaconState state, BeaconBlockHeader header, byte[] privateKey,
            BeaconStateAccessor beaconStateAccessor)
        {
            var domain = beaconStateAccessor.GetDomain(state, DomainType.BeaconProposer, Epoch.None);
            var signingRoot = header.SigningRoot();
            var signature = TestSecurity.BlsSign(signingRoot, privateKey, domain);
            header.SetSignature(signature);
        }
    }
}
