using System.Collections.Generic;
using Cortex.SimpleSerialize;
using Nethermind.BeaconNode.Containers;

namespace Nethermind.BeaconNode.Ssz
{
    public static class ValidatorExtensions
    {
        public static SszContainer ToSszContainer(this Validator item)
        {
            return new SszContainer(GetValues(item));
        }

        private static IEnumerable<SszElement> GetValues(Validator item)
        {
            yield return item.PublicKey.ToSszBasicVector();
            // Commitment to pubkey for withdrawals and transfers
            yield return item.WithdrawalCredentials.ToSszBasicVector();
            // Balance at stake
            yield return item.EffectiveBalance.ToSszBasicElement();
            yield return item.IsSlashed.ToSszBasicElement();
            // Status epochs
            // When criteria for activation were met
            yield return item.ActivationEligibilityEpoch.ToSszBasicElement();
            yield return item.ActivationEpoch.ToSszBasicElement();
            yield return item.ExitEpoch.ToSszBasicElement();
            // When validator can withdraw or transfer funds
            yield return item.WithdrawableEpoch.ToSszBasicElement();
        }
    }
}
