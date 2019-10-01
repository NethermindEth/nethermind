using System;

using Epoch = System.UInt64;
using Gwei = System.UInt64;
using Hash = System.Byte; // Byte32
using BlsPubKey = System.Byte; // Byte48

namespace Cortex.Containers
{
    public class Validator
    {
        //public BlsPubKey PubKey { get; }

        //     /// <summary>Gets the public key commitment for withdrawals and transfers</summary>
        //     public Hash WithdrawalCredentials { get; }

        //     /// <summary>Gets the balance at stake</summary>
        //     public Gwei EffectiveBalance { get; }
        //     public bool Slashed { get; }

        //     // Status Epochs

        //     /// <summary>Gets when criteria for activation were met</summary>
        //     public Epoch ActivationEligibilityEpoch { get; }

        public Epoch ActivationEpoch { get; }
        public Epoch ExitEpoch { get; }

        //     /// <summary>Gets when  validator can withdraw or transfer funds</summary>
        //     public Epoch WithdrawableEpoch { get; }

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
