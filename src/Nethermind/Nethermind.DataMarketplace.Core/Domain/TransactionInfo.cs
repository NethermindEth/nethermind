// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

[assembly: InternalsVisibleTo("Nethermind.DataMarketplace.Infrastructure")]
[assembly: InternalsVisibleTo("Nethermind.DataMarketplace.Test")]
[assembly: InternalsVisibleTo("Nethermind.DataMarketplace.Consumers.Test")]
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
            return obj.GetType() == GetType() && Equals((TransactionInfo)obj);
        }

        public override int GetHashCode()
        {
            return (Hash != null ? Hash.GetHashCode() : 0);
        }
    }
}
