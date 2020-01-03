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
using System.Drawing;
using Nethermind.Core2.Types;

namespace Nethermind.Core2.Containers
{
    public class PendingAttestation
    {
        public const int SszDynamicOffset = sizeof(uint) +
                                            AttestationData.SszLength +
                                            Core2.ByteLength.Slot +
                                            ValidatorIndex.SszLength;
        
        public static int SszLength(PendingAttestation? value)
        {
            if (value == null)
            {
                return 0;
            }
            
            return SszDynamicOffset + value.AggregationBits?.Length ?? 0;
        }
        
        public byte[]? AggregationBits { get; set; }
        public AttestationData? Data { get; set; }
        public Slot InclusionDelay { get; set; }
        public ValidatorIndex ProposerIndex { get; set; }
        
        public bool Equals(PendingAttestation other)
        {
            return Bytes.AreEqual(AggregationBits, other.AggregationBits) &&
                   Equals(Data, other.Data) &&
                   InclusionDelay == other.InclusionDelay &&
                   ProposerIndex == other.ProposerIndex;
        }

        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            return obj.GetType() == GetType() && Equals((PendingAttestation) obj);
        }

        public override int GetHashCode()
        {
            throw new NotSupportedException();
        }
    }
}