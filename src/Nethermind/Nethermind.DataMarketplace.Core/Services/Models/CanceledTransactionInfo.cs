// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.DataMarketplace.Core.Services.Models
{
    public class CanceledTransactionInfo : IEquatable<CanceledTransactionInfo>
    {
        public Keccak Hash { get; }
        public UInt256 GasPrice { get; }
        public ulong GasLimit { get; }

        public CanceledTransactionInfo(Keccak hash, UInt256 gasPrice, ulong gasLimit)
        {
            Hash = hash;
            GasPrice = gasPrice;
            GasLimit = gasLimit;
        }

        public bool Equals(CanceledTransactionInfo? other)
        {
            if (ReferenceEquals(null, other)) return false;
            return ReferenceEquals(this, other) || Equals(Hash, other.Hash);
        }

        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            return obj.GetType() == this.GetType() && Equals((CanceledTransactionInfo)obj);
        }

        public override int GetHashCode()
        {
            return (Hash != null ? Hash.GetHashCode() : 0);
        }
    }
}
