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
using Nethermind.Core.Encoding;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;

namespace Nethermind.Core2.Containers
{
    public class IndexedAttestation
    {
        public const int SszDynamicOffset = sizeof(uint) +
                                            AttestationData.SszLength +
                                            BlsSignature.SszLength;
        
        public static int SszLength(IndexedAttestation value)
        {
            return SszDynamicOffset +
                   value.AttestingIndices.Length * ValidatorIndex.SszLength;
        }

        public ValidatorIndex[] AttestingIndices { get; set; }
        public AttestationData Data { get; set; }
        public BlsSignature Signature { get; set; }
        
        public bool Equals(IndexedAttestation other)
        {
            if (!Equals(Data, other.Data) ||
                !Equals(Signature, other.Signature) ||
                AttestingIndices.Length != other.AttestingIndices.Length)
            {
                return false;
            }

            for (int i = 0; i < AttestingIndices.Length; i++)
            {
                if (AttestingIndices[i] != other.AttestingIndices[i])
                {
                    return false;
                }
            }

            return true;
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            return obj.GetType() == GetType() && Equals((IndexedAttestation) obj);
        }

        public override int GetHashCode()
        {
            throw new NotSupportedException();
        }
    }
}