using System.Collections.Generic;
using Cortex.Containers;
using Cortex.SimpleSerialize;

namespace Cortex.BeaconNode.Ssz
{
    public static class ValidatorExtensions
    {
        public static SszContainer ToSszContainer(this Validator item)
        {
            return new SszContainer(GetValues(item));
        }

        private static IEnumerable<SszElement> GetValues(Validator item)
        {
            yield return new SszBasicVector(item.PublicKey.AsSpan());
            // Commitment to pubkey for withdrawals and transfers
            yield return new SszBasicVector(item.WithdrawalCredentials.AsSpan());
            // Balance at stake
            yield return new SszBasicElement((ulong)item.EffectiveBalance);
            //slashed: boolean
            //yield return new SszBasicElement(item.IsSlashed);

            // Status epochs
            // When criteria for activation were met
            yield return new SszBasicElement((ulong)item.ActivationEligibilityEpoch);
            yield return new SszBasicElement((ulong)item.ActivationEpoch);
            yield return new SszBasicElement((ulong)item.ExitEpoch);
            // When validator can withdraw or transfer funds
            yield return new SszBasicElement((ulong)item.WithdrawableEpoch);
        }
    }
}
