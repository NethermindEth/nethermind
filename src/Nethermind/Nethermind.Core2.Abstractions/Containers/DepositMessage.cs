// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
