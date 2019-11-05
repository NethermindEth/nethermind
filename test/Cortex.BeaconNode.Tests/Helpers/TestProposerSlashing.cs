using System.Linq;
using Cortex.BeaconNode.Configuration;
using Cortex.Containers;

namespace Cortex.BeaconNode.Tests.Helpers
{
    public static class TestProposerSlashing
    {
        public static ProposerSlashing GetValidProposerSlashing(BeaconState state, bool signed1, bool signed2,
            TimeParameters timeParameters,
            BeaconStateAccessor beaconStateAccessor)
        {
            var currentEpoch = beaconStateAccessor.GetCurrentEpoch(state);
            var validatorIndex = beaconStateAccessor.GetActiveValidatorIndices(state, currentEpoch).Last();
            var validator = state.Validators[(int)(ulong)validatorIndex];
            var privateKey = TestKeys.PublicKeyToPrivateKey(validator.PublicKey, timeParameters);
            var slot = state.Slot;

            var header1 = new BeaconBlockHeader(
                slot,
                new Hash32(Enumerable.Repeat((byte)0x33, 32).ToArray()),
                new Hash32(Enumerable.Repeat((byte)0x44, 32).ToArray()),
                new Hash32(Enumerable.Repeat((byte)0x45, 32).ToArray()),
                new BlsSignature()
                );

            var header2 = new BeaconBlockHeader(
                slot,
                new Hash32(Enumerable.Repeat((byte)0x99, 32).ToArray()),
                new Hash32(Enumerable.Repeat((byte)0x44, 32).ToArray()),
                new Hash32(Enumerable.Repeat((byte)0x45, 32).ToArray()),
                new BlsSignature()
                );

            if (signed1)
            {
                TestBlockHeader.SignBlockHeader(state, header1, privateKey, beaconStateAccessor);
            }
            if (signed2)
            {
                TestBlockHeader.SignBlockHeader(state, header2, privateKey, beaconStateAccessor);
            }

            var proposerSlashing = new ProposerSlashing(validatorIndex, header1, header2);

            return proposerSlashing;
        }
    }
}
