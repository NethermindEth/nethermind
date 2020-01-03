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
    public class BeaconBlockHeader
    {
        public const int SszLength = Core2.SszLength.Slot + 3 * Hash32.SszLength + BlsSignature.SszLength;
        
        public Slot Slot { get; set; }
        public Hash32 ParentRoot { get; set; }
        public Hash32 StateRoot { get; set; }
        public Hash32 BodyRoot { get; set; }
        public BlsSignature Signature { get; set; } = BlsSignature.Empty;
        
        public bool Equals(BeaconBlockHeader other)
        {
            return Slot.Equals(other.Slot) &&
                   Equals(ParentRoot, other.ParentRoot) &&
                   Equals(StateRoot, other.StateRoot) &&
                   Equals(BodyRoot, other.BodyRoot) &&
                   Equals(Signature, other.Signature);
        }

        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            return obj.GetType() == GetType() && Equals((BeaconBlockHeader) obj);
        }

        public override int GetHashCode()
        {
            throw new NotSupportedException();
        }

    }
}