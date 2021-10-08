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
using System.Collections.Generic;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;

namespace Nethermind.Core2.Containers
{
    public class IndexedAttestation
    {
        private List<ValidatorIndex> _attestingIndices;

        public static readonly IndexedAttestation Zero =
            new IndexedAttestation(new ValidatorIndex[0], AttestationData.Zero, BlsSignature.Zero);
        
        public IndexedAttestation(
            IEnumerable<ValidatorIndex> attestingIndices,
            AttestationData data,
            BlsSignature signature)
        {
            _attestingIndices = new List<ValidatorIndex>(attestingIndices);
            Data = data;
            Signature = signature;
        }

        public IReadOnlyList<ValidatorIndex> AttestingIndices => _attestingIndices;

        public AttestationData Data { get; }

        public BlsSignature Signature { get; }

        public override string ToString()
        {
            return $"C:{Data.Index} S:{Data.Slot} Sig:{Signature.ToString().Substring(0, 12)}";
        }
        
        public bool Equals(IndexedAttestation other)
        {
            if (!Equals(Data, other.Data) ||
                !Equals(Signature, other.Signature) ||
                AttestingIndices.Count != other.AttestingIndices.Count)
            {
                return false;
            }

            for (int i = 0; i < AttestingIndices.Count; i++)
            {
                if (AttestingIndices[i] != other.AttestingIndices[i])
                {
                    return false;
                }
            }

            return true;
        }

        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            return obj is IndexedAttestation other && Equals(other);
        }

        public override int GetHashCode()
        {
            var hashCode = new HashCode();
            hashCode.Add(Data);
            hashCode.Add(Signature);
            for (int i = 0; i < AttestingIndices.Count; i++)
            {
                hashCode.Add(AttestingIndices[i]);
            }

            return hashCode.ToHashCode();
        }
        
    }
}
