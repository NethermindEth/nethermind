using System.Linq;
using Cortex.BeaconNode.Configuration;
using Cortex.Containers;

namespace Cortex.BeaconNode.Tests.Helpers
{
    public static class TestAttesterSlashing
    {
        public static AttesterSlashing GetValidAttesterSlashing(BeaconState state, bool signed1, bool signed2,
            MiscellaneousParameters miscellaneousParameters, TimeParameters timeParameters, StateListLengths stateListLengths, MaxOperationsPerBlock maxOperationsPerBlock,
            BeaconChainUtility beaconChainUtility, BeaconStateAccessor beaconStateAccessor, BeaconStateTransition beaconStateTransition)
        {
            var attestation1 = TestAttestation.GetValidAttestation(state, Slot.None, CommitteeIndex.None, signed1,
                miscellaneousParameters, timeParameters, stateListLengths, maxOperationsPerBlock,
                beaconChainUtility, beaconStateAccessor, beaconStateTransition);

            var attestation2 = TestAttestation.GetValidAttestation(state, Slot.None, CommitteeIndex.None, signed: false,
                miscellaneousParameters, timeParameters, stateListLengths, maxOperationsPerBlock,
                beaconChainUtility, beaconStateAccessor, beaconStateTransition);

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
