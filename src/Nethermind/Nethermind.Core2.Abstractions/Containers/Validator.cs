// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;

namespace Nethermind.Core2.Containers
{
    public class Validator
    {
        public static readonly Validator Zero = new Validator(BlsPublicKey.Zero, Bytes32.Zero, Gwei.Zero, false,
            Epoch.Zero, Epoch.Zero, Epoch.Zero, Epoch.Zero);

        public Validator(
            BlsPublicKey publicKey,
            Bytes32 withdrawalCredentials,
            Gwei effectiveBalance,
            bool isSlashed,
            Epoch activationEligibilityEpoch,
            Epoch activationEpoch,
            Epoch exitEpoch,
            Epoch withdrawableEpoch)
        {
            PublicKey = publicKey;
            WithdrawalCredentials = withdrawalCredentials;
            EffectiveBalance = effectiveBalance;
            IsSlashed = isSlashed;
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
        public Bytes32 WithdrawalCredentials { get; }

        public static Validator Clone(Validator other)
        {
            var clone = new Validator(
                 other.PublicKey,
                 other.WithdrawalCredentials,
                 other.EffectiveBalance,
                 other.IsSlashed,
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

        public bool Equals(Validator other)
        {
            return PublicKey.Equals(other.PublicKey) &&
                   WithdrawalCredentials.Equals(other.WithdrawalCredentials) &&
                   EffectiveBalance.Equals(other.EffectiveBalance) &&
                   IsSlashed == other.IsSlashed &&
                   ActivationEligibilityEpoch.Equals(other.ActivationEligibilityEpoch) &&
                   ActivationEpoch.Equals(other.ActivationEpoch) &&
                   ExitEpoch.Equals(other.ExitEpoch)
                   && WithdrawableEpoch.Equals(other.WithdrawableEpoch);
        }

        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            return obj.GetType() == GetType() && Equals((Validator)obj);
        }

        public override int GetHashCode()
        {
            return BinaryPrimitives.ReadInt32LittleEndian(PublicKey.Bytes);
        }
    }
}
