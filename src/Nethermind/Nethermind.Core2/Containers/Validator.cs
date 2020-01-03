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

using System.Buffers.Binary;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;

namespace Nethermind.Core2.Containers
{
    public class Validator
    {
        public const ulong ValidatorRegistryLimit = 1_099_511_627_776;

        public const int SszLength = BlsPublicKey.SszLength + Hash32.SszLength + ByteLength.Gwei + 1 + 4 * ByteLength.Epoch;

        public Validator(BlsPublicKey publicKey)
        {
            PublicKey = publicKey;
        }

        public BlsPublicKey PublicKey { get; }

        /// <summary>Gets the public key commitment for withdrawals and transfers</summary>
        public Hash32 WithdrawalCredentials { get; set; }

        /// <summary>
        ///     Balance at stake
        /// </summary>
        public Gwei EffectiveBalance { get; set; }

        public bool Slashed { get; set; }

        /// <summary>
        ///     When criteria for activation were met
        /// </summary>
        public Epoch ActivationEligibilityEpoch { get; set; }

        public Epoch ActivationEpoch { get; set; }
        public Epoch ExitEpoch { get; set; }

        /// <summary>
        ///     Can validator withdraw funds
        /// </summary>
        public Epoch WithdrawableEpoch { get; set; }

        public bool Equals(Validator other)
        {
            return PublicKey.Equals(other.PublicKey) &&
                   WithdrawalCredentials.Equals(other.WithdrawalCredentials) &&
                   EffectiveBalance.Equals(other.EffectiveBalance) &&
                   Slashed == other.Slashed &&
                   ActivationEligibilityEpoch.Equals(other.ActivationEligibilityEpoch) &&
                   ActivationEpoch.Equals(other.ActivationEpoch) &&
                   ExitEpoch.Equals(other.ExitEpoch)
                   && WithdrawableEpoch.Equals(other.WithdrawableEpoch);
        }

        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            return obj.GetType() == GetType() && Equals((Validator) obj);
        }

        public override int GetHashCode()
        {
            return BinaryPrimitives.ReadInt32LittleEndian(PublicKey.Bytes);
        }

        public bool IsSlashable(Epoch epoch)
        {
            return !Slashed && ActivationEpoch <= epoch && epoch < WithdrawableEpoch;
        }
    }
}