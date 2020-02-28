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
    public class DepositMessage : IEquatable<DepositMessage>
    {
        public DepositMessage(BlsPublicKey publicKey, Bytes32 withdrawalCredentials, Gwei amount)
        {
            PublicKey = publicKey;
            WithdrawalCredentials = withdrawalCredentials;
            Amount = amount;
        }

        public Gwei Amount { get; }

        public BlsPublicKey PublicKey { get; }

        public Bytes32 WithdrawalCredentials { get; }

        public bool Equals(DepositMessage other)
        {
            return Equals(PublicKey, other.PublicKey) &&
                   Equals(WithdrawalCredentials, other.WithdrawalCredentials) &&
                   Amount == other.Amount;
        }

        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            return obj is DepositMessage other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(PublicKey, WithdrawalCredentials, Amount);
        }

        public override string ToString()
        {
            return $"P:{PublicKey.ToString().Substring(0, 12)} A:{Amount}";
        }
    }
}