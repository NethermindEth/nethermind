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
using System.Collections;
using System.Linq;
using Nethermind.Core2.Crypto;

namespace Nethermind.Core2.Containers
{
    public class Attestation : IEquatable<Attestation>
    {
        public static readonly Attestation Zero = new Attestation(new BitArray(0), AttestationData.Zero, BlsSignature.Zero);
        
        public Attestation(BitArray aggregationBits, AttestationData data, BlsSignature signature)
        {
            AggregationBits = aggregationBits;
            Data = data;
            Signature = signature;
        }

        public BitArray AggregationBits { get; }

        public AttestationData Data { get; }

        public BlsSignature Signature { get; private set; }

        public void SetSignature(BlsSignature signature)
        {
            Signature = signature;
        }

        public override string ToString()
        {
            return $"C:{Data.Index} S:{Data.Slot} Sig:{Signature.ToString().Substring(0, 12)}";
        }
        
        public bool Equals(Attestation? other)
        {
            if (other is null ||
                !Equals(Data, other.Data) ||
                !Equals(Signature, other.Signature) ||
                AggregationBits.Count != other.AggregationBits.Count)
            {
                return false;
            }

            for (int i = 0; i < AggregationBits.Count; i++)
            {
                if (AggregationBits[i] != other.AggregationBits[i])
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
            return obj is Attestation other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(AggregationBits, Data, Signature);
        }
    }
}
