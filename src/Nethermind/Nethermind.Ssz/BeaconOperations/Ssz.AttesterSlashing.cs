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
using System.Buffers.Binary;
using Nethermind.Core2;
using Nethermind.Core2.Containers;

namespace Nethermind.Ssz
{
    public static partial class Ssz
    {
        public static void Encode(Span<byte> span, AttesterSlashing? container)
        {
            if (span.Length != ByteLength.AttesterSlashingLength(container))
            {
                ThrowTargetLength<AttesterSlashing>(span.Length, ByteLength.AttesterSlashingLength(container));
            }

            if (container == null)
            {
                return;
            }

            int dynamicOffset = 2 * VarOffsetSize;
            int length1 = ByteLength.IndexedAttestationLength(container.Attestation1);
            Encode(span.Slice(0, VarOffsetSize), dynamicOffset);
            Encode(span.Slice(dynamicOffset, length1), container.Attestation1);

            dynamicOffset += ByteLength.IndexedAttestationLength(container.Attestation1);
            int length2 = ByteLength.IndexedAttestationLength(container.Attestation2);
            Encode(span.Slice(VarOffsetSize, VarOffsetSize), dynamicOffset);
            Encode(span.Slice(dynamicOffset, length2), container.Attestation2);
        }

        public static AttesterSlashing? DecodeAttesterSlashing(Span<byte> span)
        {
            if (span.Length == 0)
            {
                return null;
            }

            int offset1 = (int) DecodeUInt(span.Slice(0, VarOffsetSize));
            int offset2 = (int) DecodeUInt(span.Slice(VarOffsetSize, VarOffsetSize));

            int length1 = offset2 - offset1;
            int length2 = span.Length - offset2;

            IndexedAttestation attestation1 = DecodeIndexedAttestation(span.Slice(offset1, length1));
            IndexedAttestation attestation2 = DecodeIndexedAttestation(span.Slice(offset2, length2));

            AttesterSlashing attesterSlashing = new AttesterSlashing(attestation1, attestation2);

            return attesterSlashing;
        }

        public static void Encode(Span<byte> span, AttesterSlashing?[]? containers)
        {
            if (containers is null)
            {
                return;
            }
            
            int offset = 0;
            int dynamicOffset = containers.Length * VarOffsetSize;
            for (int i = 0; i < containers.Length; i++)
            {
                int currentLength = ByteLength.AttesterSlashingLength(containers[i]);
                Encode(span.Slice(offset, VarOffsetSize), dynamicOffset);
                Encode(span.Slice(dynamicOffset, currentLength), containers[i]);
                offset += VarOffsetSize;
                dynamicOffset += currentLength;
            }
        }

        public static AttesterSlashing?[] DecodeAttesterSlashings(Span<byte> span)
        {
            if (span.Length == 0)
            {
                return Array.Empty<AttesterSlashing>();
            }

            int offset = 0;
            int dynamicOffset = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(0, VarOffsetSize));
            offset += 4;

            int itemsCount = dynamicOffset / VarOffsetSize;
            AttesterSlashing?[] containers = new AttesterSlashing?[itemsCount];
            for (int i = 0; i < itemsCount; i++)
            {
                int nextDynamicOffset = i == itemsCount - 1 ? span.Length : BinaryPrimitives.ReadInt32LittleEndian(span.Slice(offset, VarOffsetSize));
                int length = nextDynamicOffset - dynamicOffset;
                AttesterSlashing? container = DecodeAttesterSlashing(span.Slice(dynamicOffset, length));
                containers[i] = container;
                dynamicOffset = nextDynamicOffset;
                offset += VarOffsetSize;
            }

            return containers;
        }
        
        private static void Encode(Span<byte> span, AttesterSlashing?[]? containers, ref int offset, ref int dynamicOffset)
        {
            int length = (containers?.Length ?? 0)  * VarOffsetSize;
            if (!(containers is null))
            {
                for (int i = 0; i < containers.Length; i++)
                {
                    length += ByteLength.AttesterSlashingLength(containers[i]);
                }
            }

            Encode(span.Slice(offset, VarOffsetSize), dynamicOffset);
            Encode(span.Slice(dynamicOffset, length), containers);
            dynamicOffset += length;
            offset += VarOffsetSize;
        }
    }
}