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
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Mev.Data
{
    public class MevMegabundle: MevBundle, IEquatable<MevMegabundle>
    {
        public MevMegabundle(Signature relaySignature, long blockNumber, IReadOnlyList<BundleTransaction> transactions, UInt256? minTimestamp = null, UInt256? maxTimestamp = null)
        : base(blockNumber, transactions, minTimestamp, maxTimestamp)
        {
            RelaySignature = relaySignature;
        }
        
        public Signature RelaySignature { get; }

        public Address? RelayAddress { get; internal set; }

        public bool Equals(MevMegabundle? other)
        {
            if (ReferenceEquals(null, other)) return false;
            if (ReferenceEquals(this, other)) return true;
            return Equals(Hash, other.Hash)
                && Equals(RelaySignature, other.RelaySignature);
        }
        
        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != GetType()) return false;
            return Equals((MevMegabundle) obj);
        }

        public override int GetHashCode() => HashCode.Combine(Hash, RelaySignature).GetHashCode();
        
        public override string ToString() => $"Hash:{Hash}; Block:{BlockNumber}; Min:{MinTimestamp}; Max:{MaxTimestamp}; TxCount:{Transactions.Count}; RelaySignature:{RelaySignature};";
    }
}
