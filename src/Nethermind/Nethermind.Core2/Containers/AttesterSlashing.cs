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

namespace Nethermind.Core2.Containers
{
    public class AttesterSlashing
    {
        public IndexedAttestation? Attestation1 { get; set; }
        public IndexedAttestation? Attestation2 { get; set; }

        public static int SszLength(AttesterSlashing? container)
        {
            if (container is null)
            {
                return 0;
            }
            
            return 2 * sizeof(uint) +
                   ByteLength.IndexedAttestationLength(container.Attestation1) +
                   ByteLength.IndexedAttestationLength(container.Attestation2);
        }
        
        public bool Equals(AttesterSlashing other)
        {
            return Equals(Attestation1, other.Attestation1) &&
                   Equals(Attestation2, other.Attestation2);
        }

        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            return obj.GetType() == GetType() && Equals((AttesterSlashing) obj);
        }

        public override int GetHashCode()
        {
            throw new NotSupportedException();
        }
    }
}