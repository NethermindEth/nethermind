namespace Cortex.Containers
{
    public class Validator
    {
        public Validator(BlsPublicKey publicKey, Hash32 withdrawalCredentials, Epoch activationEligibilityEpoch, Epoch activationEpoch,
            Epoch exitEpoch, Epoch withdrawableEpoch, Gwei effectiveBalance)
        {
            PublicKey = publicKey;
            WithdrawalCredentials = withdrawalCredentials;
            ActivationEligibilityEpoch = activationEligibilityEpoch;
            ActivationEpoch = activationEpoch;
            ExitEpoch = exitEpoch;
            WithdrawableEpoch = withdrawableEpoch;
            EffectiveBalance = effectiveBalance;
        }

        /// <summary>Gets when criteria for activation were met</summary>
        public Epoch ActivationEligibilityEpoch { get; private set; }

        public bool Slashed { get; }

        public Epoch ActivationEpoch { get; private set; }

        /// <summary>Gets the balance at stake</summary>
        public Gwei EffectiveBalance { get; private set; }

        public Epoch ExitEpoch { get; }

        public BlsPublicKey PublicKey { get; }

        /// <summary>Gets when  validator can withdraw or transfer funds</summary>
        public Epoch WithdrawableEpoch { get; }

        /// <summary>Gets the public key commitment for withdrawals and transfers</summary>
        public Hash32 WithdrawalCredentials { get; }

        public void SetActive(Epoch activationEpoch)
        {
            ActivationEpoch = activationEpoch;
        }

        public void SetEffectiveBalance(Gwei effectiveBalance)
        {
            EffectiveBalance = effectiveBalance;
        }

        public void SetEligible(Epoch activationEligibilityEpoch)
        {
            ActivationEligibilityEpoch = activationEligibilityEpoch;
        }

        /// <summary>
        /// Check if ``validator`` is active.
        /// </summary>
        internal bool IsActiveValidator(Epoch epoch)
        {
            return ActivationEpoch <= epoch
                && epoch < ExitEpoch;
        }
    }
}
