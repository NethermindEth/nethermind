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
using Nethermind.Core2.Types;

namespace Nethermind.Core2.Containers
{
    public class ProposerSlashing
    {
        public ProposerSlashing(
            ValidatorIndex proposerIndex,
            BeaconBlockHeader header1,
            BeaconBlockHeader header2)
        {
            ProposerIndex = proposerIndex;
            Header1 = header1;
            Header2 = header2;
        }

        public BeaconBlockHeader Header1 { get; }
        public BeaconBlockHeader Header2 { get; }
        public ValidatorIndex ProposerIndex { get; }

        public override string ToString()
        {
            return $"P:{ProposerIndex} for B1:({Header1})";
        }
        
        public bool Equals(ProposerSlashing other)
        {
            return ProposerIndex == other.ProposerIndex &&
                   Equals(Header1, other.Header1) &&
                   Equals(Header2, other.Header2);
        }

        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            return obj is ProposerSlashing other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(ProposerIndex, Header1, Header2);
        }
    }
}
