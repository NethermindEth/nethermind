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
    public class ProposerSlashing
    {
        public static readonly ProposerSlashing Zero = new ProposerSlashing(ValidatorIndex.Zero,
            SignedBeaconBlockHeader.Zero, SignedBeaconBlockHeader.Zero);

        public ProposerSlashing(
            ValidatorIndex proposerIndex,
            SignedBeaconBlockHeader signedHeader1,
            SignedBeaconBlockHeader signedHeader2)
        {
            ProposerIndex = proposerIndex;
            SignedHeader1 = signedHeader1;
            SignedHeader2 = signedHeader2;
        }

        public SignedBeaconBlockHeader SignedHeader1 { get; }
        public SignedBeaconBlockHeader SignedHeader2 { get; }
        public ValidatorIndex ProposerIndex { get; }

        public override string ToString()
        {
            return $"P:{ProposerIndex} for B1:({SignedHeader1})";
        }
        
        public bool Equals(ProposerSlashing other)
        {
            return ProposerIndex == other.ProposerIndex &&
                   Equals(SignedHeader1, other.SignedHeader1) &&
                   Equals(SignedHeader2, other.SignedHeader2);
        }

        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            return obj is ProposerSlashing other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(ProposerIndex, SignedHeader1, SignedHeader2);
        }
    }
}
