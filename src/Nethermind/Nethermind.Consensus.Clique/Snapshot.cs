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

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.Clique
{
    public class Snapshot : ICloneable
    {
        public long Number { get; set; }
        public Keccak Hash { get; set; }
        public SortedList<Address, long> Signers { get; }
        
        public List<Vote> Votes;
        internal Dictionary<Address, Tally> Tally { get; }

        internal Snapshot(long number, Keccak hash, SortedList<Address, long> signers, Dictionary<Address, Tally> tally)
        {
            Number = number;
            Hash = hash;
            Signers = new SortedList<Address, long>(signers, AddressComparer.Instance);
            Votes = new List<Vote>();
            Tally = tally;
        }

        internal Snapshot(long number, Keccak hash, SortedList<Address, long> signers)
            : this(number, hash, signers, new Dictionary<Address, Tally>())
        {
        }

        public object Clone()
        {
            Snapshot clone = new Snapshot(Number, Hash, new SortedList<Address, long>(Signers, AddressComparer.Instance), new Dictionary<Address, Tally>(Tally));
            clone.Votes = new List<Vote>(Votes);
            return clone;
        }
        
        public long SignerLimit => Signers.Count / 2 + 1;
    }
}
