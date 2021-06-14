//  Copyright (c) 2021 Demerzel Solutions Limited
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
// 

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Mev.Data
{
    public class MevBundle : IEquatable<MevBundle>
    {
        public MevBundle(IReadOnlyList<Transaction> transactions, long blockNumber, UInt256? minTimestamp, UInt256? maxTimestamp, Keccak[]? revertingTxHashes = null)
        {
            Transactions = transactions;
            BlockNumber = blockNumber;
            RevertingTxHashes = revertingTxHashes ?? Array.Empty<Keccak>();
            MinTimestamp = minTimestamp ?? UInt256.Zero;
            MaxTimestamp = maxTimestamp ?? UInt256.Zero;
            
            Keccak[] missingRevertingTxHashes = RevertingTxHashes.Except(transactions.Select(t => t.Hash!)).ToArray();
            if (missingRevertingTxHashes.Length > 0)
            {
                throw new ArgumentException(
                    $"Bundle didn't contain some of revertingTxHashes: [{string.Join(", ", missingRevertingTxHashes.OfType<object>())}]",
                    nameof(revertingTxHashes));
            }
        }

        public IReadOnlyList<Transaction> Transactions { get; }

        public long BlockNumber { get; }
        public Keccak[] RevertingTxHashes { get; }

        public UInt256 MaxTimestamp { get; }
        
        public UInt256 MinTimestamp { get; }

        public bool Equals(MevBundle? other)
        {
            if (ReferenceEquals(null, other)) return false;
            if (ReferenceEquals(this, other)) return true;
            return Transactions.Select(t => t.Hash).SequenceEqual(other.Transactions.Select(t => t.Hash))
                   && BlockNumber == other.BlockNumber
                   && MaxTimestamp.Equals(other.MaxTimestamp)
                   && MinTimestamp.Equals(other.MinTimestamp);
        }

        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != GetType()) return false;
            return Equals((MevBundle) obj);
        }

        public override int GetHashCode()
        {
            HashCode hashCode = new();
            hashCode.Add(BlockNumber);
            hashCode.Add(MaxTimestamp);
            hashCode.Add(MinTimestamp);
            for (int i = 0; i < Transactions.Count; i++)
            {
                hashCode.Add(Transactions[i].Hash);
            }

            return hashCode.ToHashCode();
        }

        public static MevBundle Empty(long blockNumber, UInt256 minTimestamp, UInt256 maxTimestamp) =>
            new(Array.Empty<Transaction>(), blockNumber, minTimestamp, maxTimestamp);

        public override string ToString() => $"Block:{BlockNumber}; Min:{MinTimestamp}; Max:{MaxTimestamp}; TxCount:{Transactions.Count};";
    }
}
