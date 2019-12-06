using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nethermind.BeaconNode.Configuration;
using Nethermind.BeaconNode.Containers;
using Nethermind.BeaconNode.Ssz;

namespace Nethermind.BeaconNode.Tests.Helpers
{
    public static class TestVoluntaryExit
    {
        public static VoluntaryExit BuildVoluntaryExit(IServiceProvider testServiceProvider, BeaconState state, Epoch epoch, ValidatorIndex validatorIndex, byte[] privateKey, bool signed)
        {
            var voluntaryExit = new VoluntaryExit(epoch, validatorIndex, new BlsSignature());
            if (signed)
            {
                SignVoluntaryExit(testServiceProvider, state, voluntaryExit, privateKey);
            }
            return voluntaryExit;
        }

        public static void SignVoluntaryExit(IServiceProvider testServiceProvider, BeaconState state, VoluntaryExit voluntaryExit, byte[] privateKey)
        {
            var signatureDomains = testServiceProvider.GetService<IOptions<SignatureDomains>>().Value;
            var beaconStateAccessor = testServiceProvider.GetService<BeaconStateAccessor>();

            var domain = beaconStateAccessor.GetDomain(state, signatureDomains.VoluntaryExit, voluntaryExit.Epoch);
            var signature = TestSecurity.BlsSign(voluntaryExit.SigningRoot(), privateKey, domain);
            voluntaryExit.SetSignature(signature);
        }
    }
}
