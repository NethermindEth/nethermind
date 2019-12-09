using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;
using Hash32 = Nethermind.Core2.Types.Hash32;

namespace Nethermind.BeaconNode.Containers
{
    public class Validator
    {
        public Validator(
            BlsPublicKey publicKey,
            Hash32 withdrawalCredentials,
            Gwei effectiveBalance,
            //bool slashed,
            Epoch activationEligibilityEpoch,
            Epoch activationEpoch,
            Epoch exitEpoch,
            Epoch withdrawableEpoch)
        {
            PublicKey = publicKey;
            WithdrawalCredentials = withdrawalCredentials;
            EffectiveBalance = effectiveBalance;
            ActivationEligibilityEpoch = activationEligibilityEpoch;
            ActivationEpoch = activationEpoch;
            ExitEpoch = exitEpoch;
            WithdrawableEpoch = withdrawableEpoch;
        }

        /// <summary>Gets when criteria for activation were met</summary>
        public Epoch ActivationEligibilityEpoch { get; private set; }

        public Epoch ActivationEpoch { get; private set; }

        /// <summary>Gets the balance at stake</summary>
        public Gwei EffectiveBalance { get; private set; }

        public Epoch ExitEpoch { get; private set; }

        public bool IsSlashed { get; private set; }

        public BlsPublicKey PublicKey { get; }

        /// <summary>Gets when  validator can withdraw or transfer funds</summary>
        public Epoch WithdrawableEpoch { get; private set; }

        /// <summary>Gets the public key commitment for withdrawals and transfers</summary>
        public Hash32 WithdrawalCredentials { get; }

        public static Validator Clone(Validator other)
        {
            var clone = new Validator(
                 other.PublicKey,
                 other.WithdrawalCredentials,
                 other.EffectiveBalance,
                 other.ActivationEligibilityEpoch,
                 other.ActivationEpoch,
                 other.ExitEpoch,
                 other.WithdrawableEpoch
                );
            return clone;
        }

        public void SetActive(Epoch activationEpoch) => ActivationEpoch = activationEpoch;

        public void SetEffectiveBalance(Gwei effectiveBalance) => EffectiveBalance = effectiveBalance;

        public void SetEligible(Epoch activationEligibilityEpoch) => ActivationEligibilityEpoch = activationEligibilityEpoch;

        public void SetSlashed() => IsSlashed = true;

        public void SetWithdrawableEpoch(Epoch withdrawableEpoch) => WithdrawableEpoch = withdrawableEpoch;

        public void SetExitEpoch(Epoch exitEpoch) => ExitEpoch = exitEpoch;
    }
}
