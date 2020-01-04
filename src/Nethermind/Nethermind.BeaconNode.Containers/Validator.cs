//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;
using Hash32 = Nethermind.Core2.Crypto.Hash32;

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
