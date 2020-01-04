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
        public const int SszLength = ByteLength.ValidatorIndexLength + 2 * BeaconBlockHeader.SszLength;

        public ValidatorIndex ProposerIndex { get; set; }
        public BeaconBlockHeader? Header1 { get; set; }
        public BeaconBlockHeader? Header2 { get; set; }

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
            return obj.GetType() == GetType() && Equals((ProposerSlashing) obj);
        }

        public override int GetHashCode()
        {
            throw new NotSupportedException();
        }
    }
}