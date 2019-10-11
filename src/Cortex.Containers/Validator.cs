namespace Cortex.Containers
{
    public class Validator
    {
        public Validator(BlsPublicKey publicKey, Hash32 withdrawalCredentials, Epoch activationEligibilityEpoch, Epoch activationEpoch,
            Epoch exitEpoch, Epoch withdrawableEpoch, ulong effectiveBalance)
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
        public Epoch ActivationEligibilityEpoch { get; }

        //public bool Slashed { get; }
        public Epoch ActivationEpoch { get; }

        /// <summary>Gets the balance at stake</summary>
        public Gwei EffectiveBalance { get; }

        public Epoch ExitEpoch { get; }

        public BlsPublicKey PublicKey { get; }

        /// <summary>Gets when  validator can withdraw or transfer funds</summary>
        public Epoch WithdrawableEpoch { get; }

        /// <summary>Gets the public key commitment for withdrawals and transfers</summary>
        public Hash32 WithdrawalCredentials { get; }

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
