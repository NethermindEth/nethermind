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
using Nethermind.Core2.Containers;

namespace Nethermind.Ssz
{
    public static partial class Ssz
    {
        public static void Encode(Span<byte> span, Attestation container)
        {
            if (span.Length != Attestation.SszLength(container)) ThrowTargetLength<Attestation>(span.Length, Attestation.SszLength(container));
            if (container == null) return;
            int offset = 0;
            int dynamicOffset = Attestation.SszDynamicOffset;
            Encode(span, container.AggregationBits, ref offset, ref dynamicOffset);
            Encode(span, container.Data, ref offset);
            Encode(span, container.Signature, ref offset);
        }

        public static void Encode(Span<byte> span, Attestation[] containers)
        {
            int offset = 0;
            int dynamicOffset = containers.Length * VarOffsetSize;
            for (int i = 0; i < containers.Length; i++)
            {
                int currentLength = Attestation.SszLength(containers[i]);
                Encode(span.Slice(offset, VarOffsetSize), dynamicOffset);
                Encode(span.Slice(dynamicOffset, currentLength), containers[i]);
                offset += VarOffsetSize;
                dynamicOffset += currentLength;
            }
        }

        public static Attestation[] DecodeAttestations(Span<byte> span)
        {
            if (span.Length == 0)
            {
                return Array.Empty<Attestation>();
            }

            int offset = 0;
            int dynamicOffset = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(0, VarOffsetSize));
            offset += VarOffsetSize;

            int itemsCount = dynamicOffset / VarOffsetSize;
            Attestation[] containers = new Attestation[itemsCount];
            for (int i = 0; i < itemsCount; i++)
            {
                int nextDynamicOffset = i == itemsCount - 1 ? span.Length : BinaryPrimitives.ReadInt32LittleEndian(span.Slice(offset, VarOffsetSize));
                int length = nextDynamicOffset - dynamicOffset;
                Attestation container = DecodeAttestation(span.Slice(dynamicOffset, length));
                containers[i] = container;
                dynamicOffset = nextDynamicOffset;
                offset += VarOffsetSize;
            }

            return containers;
        }

        public static Attestation DecodeAttestation(Span<byte> span)
        {
            if (span.Length == 0) return null;
            int offset = 0;
            Attestation container = new Attestation();

            // static part
            DecodeDynamicOffset(span, ref offset, out int dynamicOffset1);
            container.Data = DecodeAttestationData(span, ref offset);
            container.Signature = DecodeBlsSignature(span, ref offset);

            // var part
            container.AggregationBits = span.Slice(dynamicOffset1, span.Length - dynamicOffset1).ToArray();

            return container;
        }

        private static void Encode(Span<byte> span, Attestation[] attestations, ref int offset, ref int dynamicOffset)
        {
            int length = attestations.Length * VarOffsetSize;
            for (int i = 0; i < attestations.Length; i++)
            {
                length += Attestation.SszLength(attestations[i]);
            }

            Encode(span.Slice(offset, VarOffsetSize), dynamicOffset);
            Encode(span.Slice(dynamicOffset, length), attestations);
            dynamicOffset += length;
            offset += VarOffsetSize;
        }
    }
}