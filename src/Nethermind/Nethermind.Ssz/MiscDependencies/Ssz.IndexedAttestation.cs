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
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;

namespace Nethermind.Ssz
{
    public static partial class Ssz
    {
        public static void Encode(Span<byte> span, IndexedAttestation container)
        {
            if (span.Length != IndexedAttestation.SszLength(container))
            {
                ThrowTargetLength<IndexedAttestation>(span.Length, IndexedAttestation.SszLength(container));
            }

            int offset = 0;
            int dynamicOffset = 2 * VarOffsetSize + AttestationData.SszLength + BlsSignature.SszLength;
            int lengthBits0 = container.CustodyBit0Indices.Length * ValidatorIndex.SszLength;
            Encode(span.Slice(offset, VarOffsetSize), dynamicOffset);
            Encode(span.Slice(dynamicOffset, lengthBits0), container.CustodyBit0Indices);
            offset += VarOffsetSize;
            dynamicOffset += lengthBits0;
            Encode(span.Slice(offset, VarOffsetSize), dynamicOffset);
            Encode(span.Slice(dynamicOffset), container.CustodyBit1Indices);
            offset += VarOffsetSize;
            Encode(span.Slice(offset, AttestationData.SszLength), container.Data);
            offset += AttestationData.SszLength;
            Encode(span, container.Signature, ref offset);
        }

        public static IndexedAttestation DecodeIndexedAttestation(Span<byte> span)
        {
            IndexedAttestation container = new IndexedAttestation();
            uint bits0Offset = DecodeUInt(span.Slice(0, VarOffsetSize));
            uint bits1Offset = DecodeUInt(span.Slice(VarOffsetSize, VarOffsetSize));

            uint bits0Length = bits1Offset - bits0Offset;
            uint bits1Length = (uint) span.Length - bits1Offset;

            container.CustodyBit0Indices = DecodeValidatorIndexes(span.Slice((int) bits0Offset, (int) bits0Length));
            container.CustodyBit1Indices = DecodeValidatorIndexes(span.Slice((int) bits1Offset, (int) bits1Length));
            container.Data = DecodeAttestationData(span.Slice(2 * VarOffsetSize, AttestationData.SszLength));
            container.Signature = DecodeBlsSignature(span.Slice(2 * VarOffsetSize + AttestationData.SszLength, BlsSignature.SszLength));

            return container;
        }
    }
}