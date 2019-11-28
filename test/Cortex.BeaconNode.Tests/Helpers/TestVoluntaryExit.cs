using System;
using Cortex.BeaconNode.Ssz;
using Cortex.Containers;
using Microsoft.Extensions.DependencyInjection;

namespace Cortex.BeaconNode.Tests.Helpers
{
    public static class TestVoluntaryExit
    {
        public static VoluntaryExit BuildVoluntaryExit(IServiceProvider testServiceProvider, BeaconState state, Epoch epoch, ValidatorIndex validatorIndex, byte[] privateKey, bool signed)
        {
            var beaconStateAccessor = testServiceProvider.GetService<BeaconStateAccessor>();

            var voluntaryExit = new VoluntaryExit(epoch, validatorIndex, new BlsSignature());
            if (signed)
            {
                SignVoluntaryExit(state, voluntaryExit, privateKey, beaconStateAccessor);
            }
            return voluntaryExit;
        }

        public static void SignVoluntaryExit(BeaconState state, VoluntaryExit voluntaryExit, byte[] privateKey,
            BeaconStateAccessor beaconStateAccessor)
        {
            var domain = beaconStateAccessor.GetDomain(state, DomainType.VoluntaryExit, voluntaryExit.Epoch);
            var signature = TestSecurity.BlsSign(voluntaryExit.SigningRoot(), privateKey, domain);
            voluntaryExit.SetSignature(signature);
        }
    }
}
