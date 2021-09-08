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
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Mev.Source;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Mev.Data
{
    public partial class MevBundle : IEquatable<MevBundle>
    {
        private static int _sequenceNumber = 0;

        public MevBundle(long blockNumber, IReadOnlyList<BundleTransaction> transactions, UInt256? minTimestamp = null, UInt256? maxTimestamp = null)
        {
            Transactions = transactions;
            BlockNumber = blockNumber;

            Hash = GetHash(this);
            for (int i = 0; i < transactions.Count; i++)
            {
                transactions[i].BundleHash = Hash;
            }

            MinTimestamp = minTimestamp ?? UInt256.Zero;
            MaxTimestamp = maxTimestamp ?? UInt256.Zero;
            SequenceNumber = Interlocked.Increment(ref _sequenceNumber);
        }
        
        public IReadOnlyList<BundleTransaction> Transactions { get; }

        public long BlockNumber { get; }
        
        public UInt256 MaxTimestamp { get; }
        
        public UInt256 MinTimestamp { get; }
        
        public Keccak Hash { get; }

        public int SequenceNumber { get; }

        public bool Equals(MevBundle? other)
        {
            if (ReferenceEquals(null, other)) return false;
            if (ReferenceEquals(this, other)) return true;
            return Equals(Hash, other.Hash);
        }

        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != GetType()) return false;
            return Equals((MevBundle) obj);
        }

        public override int GetHashCode() => Hash.GetHashCode();

        public override string ToString() => $"Hash:{Hash}; Block:{BlockNumber}; Min:{MinTimestamp}; Max:{MaxTimestamp}; TxCount:{Transactions.Count};";
    }
}
