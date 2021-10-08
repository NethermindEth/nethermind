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
using System.Collections;
using Nethermind.Core2;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Types;

namespace Nethermind.Ssz
{
    public static partial class Ssz
    {
        public const int PendingAttestationDynamicOffset = sizeof(uint) +
                                                           Ssz.AttestationDataLength +
                                                           Ssz.SlotLength +
                                                           Ssz.ValidatorIndexLength;

        public static int PendingAttestationLength(PendingAttestation value)
        {
            return Ssz.PendingAttestationDynamicOffset + (value.AggregationBits.Length + 8) / 8;
        }

        public static void Encode(Span<byte> span, PendingAttestation container)
        {
            if (span.Length != Ssz.PendingAttestationLength(container)) ThrowTargetLength<PendingAttestation>(span.Length, Ssz.PendingAttestationLength(container));
            int offset = 0;
            int dynamicOffset = Ssz.PendingAttestationDynamicOffset;
            Encode(span, container.AggregationBits, ref offset, ref dynamicOffset);
            Encode(span, container.Data, ref offset);
            Encode(span, container.InclusionDelay, ref offset);
            Encode(span, container.ProposerIndex, ref offset);
        }

        public static void Encode(Span<byte> span, PendingAttestation[] containers)
        {
            int offset = 0;
            int dynamicOffset = containers.Length * VarOffsetSize;
            for (int i = 0; i < containers.Length; i++)
            {
                int currentLength = Ssz.PendingAttestationLength(containers[i]);
                Encode(span.Slice(offset, VarOffsetSize), dynamicOffset);
                Encode(span.Slice(dynamicOffset, currentLength), containers[i]);
                offset += VarOffsetSize;
                dynamicOffset += currentLength;
            }
        }

        public static PendingAttestation DecodePendingAttestation(Span<byte> span)
        {
            int offset = 0;
            DecodeDynamicOffset(span, ref offset, out int dynamicOffset);
            BitArray aggregationBits = DecodeBitlist(span.Slice(dynamicOffset, span.Length - dynamicOffset));
            AttestationData data = DecodeAttestationData(span, ref offset);
            Slot inclusionDelay = DecodeSlot(span, ref offset);
            ValidatorIndex proposerIndex = DecodeValidatorIndex(span, ref offset);
            PendingAttestation pendingAttestation = new PendingAttestation(aggregationBits, data, inclusionDelay, proposerIndex);
            return pendingAttestation;
        }

        public static PendingAttestation[] DecodePendingAttestations(Span<byte> span)
        {
            if (span.Length == 0)
            {
                return Array.Empty<PendingAttestation>();
            }

            int offset = 0;
            DecodeDynamicOffset(span, ref offset, out int dynamicOffset);

            int itemsCount = dynamicOffset / VarOffsetSize;
            PendingAttestation[] containers = new PendingAttestation[itemsCount];
            for (int i = 0; i < itemsCount; i++)
            {
                int nextDynamicOffset = i == itemsCount - 1 ? span.Length : BinaryPrimitives.ReadInt32LittleEndian(span.Slice(offset, VarOffsetSize));
                int length = nextDynamicOffset - dynamicOffset;
                PendingAttestation container = DecodePendingAttestation(span.Slice(dynamicOffset, length));
                containers[i] = container;
                dynamicOffset = nextDynamicOffset;
                offset += VarOffsetSize;
            }

            return containers;
        }
    }
}