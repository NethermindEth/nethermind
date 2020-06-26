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

using System;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;

namespace Nethermind.Core2.Containers
{
    public class DepositData : IEquatable<DepositData>
    {
        public Ref<DepositData> OrRoot => new Ref<DepositData>(this);

        public static readonly DepositData Zero = new DepositData(BlsPublicKey.Zero, Bytes32.Zero, Gwei.Zero,
            BlsSignature.Zero);
        
        public DepositData(BlsPublicKey publicKey, Bytes32 withdrawalCredentials, Gwei amount, BlsSignature signature)
        {
            PublicKey = publicKey;
            WithdrawalCredentials = withdrawalCredentials;
            Amount = amount;
            Signature = signature; // Signing over DepositMessage
        }

        public Gwei Amount { get; }

        public BlsPublicKey PublicKey { get; }

        /// <summary>
        /// Signing over DepositMessage
        /// </summary>
        public BlsSignature Signature { get; private set; }

        public Bytes32 WithdrawalCredentials { get; }

        public void SetSignature(BlsSignature signature)
        {
            Signature = signature;
        }

        public override string ToString()
        {
            return $"P:{PublicKey.ToString().Substring(0, 12)} A:{Amount}";
        }

        public bool Equals(DepositData other)
        {
            return Equals(PublicKey, other.PublicKey) &&
                   Equals(WithdrawalCredentials, other.WithdrawalCredentials) &&
                   Amount == other.Amount &&
                   Equals(Signature, other.Signature);
        }

        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            return obj is DepositData other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(PublicKey, WithdrawalCredentials, Amount, Signature);
        }
    }
}
