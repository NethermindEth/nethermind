using System;
using System.Linq;
using Cortex.BeaconNode.Configuration;
using Cortex.Containers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Cortex.BeaconNode.Tests.Helpers
{
    public static class TestAttesterSlashing
    {
        public static AttesterSlashing GetValidAttesterSlashing(IServiceProvider testServiceProvider, BeaconState state, bool signed1, bool signed2)
        {
            var timeParameters = testServiceProvider.GetService<IOptions<TimeParameters>>().Value;
            var beaconStateAccessor = testServiceProvider.GetService<BeaconStateAccessor>();

            var attestation1 = TestAttestation.GetValidAttestation(testServiceProvider, state, Slot.None, CommitteeIndex.None, signed1);

            var attestation2 = TestAttestation.GetValidAttestation(testServiceProvider, state, Slot.None, CommitteeIndex.None, signed: false);

            attestation2.Data.Target.SetRoot(new Hash32(Enumerable.Repeat((byte)0x01, 32).ToArray()));
            if (signed2)
            {
                TestAttestation.SignAttestation(state, attestation2, timeParameters, beaconStateAccessor);
            }

            var indexedAttestation1 = beaconStateAccessor.GetIndexedAttestation(state, attestation1);
            var indexedAttestation2 = beaconStateAccessor.GetIndexedAttestation(state, attestation2);

            var attesterSlashing = new AttesterSlashing(indexedAttestation1, indexedAttestation2);
            return attesterSlashing;
        }
    }
}
