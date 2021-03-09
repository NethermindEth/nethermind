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
using System.Runtime.CompilerServices;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

[assembly:InternalsVisibleTo("Nethermind.DataMarketplace.Infrastructure")]
[assembly:InternalsVisibleTo("Nethermind.DataMarketplace.Test")]
[assembly:InternalsVisibleTo("Nethermind.DataMarketplace.Consumers.Test")]
namespace Nethermind.DataMarketplace.Core.Domain
{
    public class TransactionInfo : IEquatable<TransactionInfo>
    {
        public Keccak? Hash { get; }
        public UInt256 Value { get; }
        public UInt256 GasPrice { get; }
        public ulong GasLimit { get; }
        public ulong Timestamp { get; }
        public TransactionType Type { get; }
        public TransactionState State { get; private set; }

        internal TransactionInfo(Keccak? hash, UInt256 value, UInt256 gasPrice, ulong gasLimit, ulong timestamp,
            TransactionType type = TransactionType.Default, TransactionState state = TransactionState.Pending)
        {
            Hash = hash;
            Value = value;
            GasPrice = gasPrice;
            GasLimit = gasLimit;
            Timestamp = timestamp;
            Type = type;
            State = state;
        }

        public static TransactionInfo Default(Keccak? hash, UInt256 value, UInt256 gasPrice, ulong gasLimit,
            ulong timestamp)
            => new TransactionInfo(hash, value, gasPrice, gasLimit, timestamp);

        public static TransactionInfo SpeedUp(Keccak? hash, UInt256 value, UInt256 gasPrice, ulong gasLimit,
            ulong timestamp)
            => new TransactionInfo(hash, value, gasPrice, gasLimit, timestamp, TransactionType.SpeedUp);

        public static TransactionInfo Cancellation(Keccak? hash, UInt256 gasPrice, ulong gasLimit, ulong timestamp)
            => new TransactionInfo(hash, 0, gasPrice, gasLimit, timestamp, TransactionType.Cancellation);

        public void SetIncluded()
        {
            State = TransactionState.Included;
        }

        public void SetRejected()
        {
            State = TransactionState.Rejected;
        }

        public bool Equals(TransactionInfo? other)
        {
            if (ReferenceEquals(null, other)) return false;
            return ReferenceEquals(this, other) || Equals(Hash, other.Hash);
        }

        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            return obj.GetType() == GetType() && Equals((TransactionInfo) obj);
        }

        public override int GetHashCode()
        {
            return (Hash != null ? Hash.GetHashCode() : 0);
        }
    }
}
