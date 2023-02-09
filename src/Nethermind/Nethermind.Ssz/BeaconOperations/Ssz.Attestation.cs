// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections;
using System.Collections.Generic;
using Nethermind.Core2;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;

namespace Nethermind.Ssz
{
    public static partial class Ssz
    {
        public const int AttestationDynamicOffset = sizeof(uint) + Ssz.AttestationDataLength + Ssz.BlsSignatureLength;

        public static int AttestationLength(Attestation container)
        {
            return AttestationDynamicOffset + (container.AggregationBits.Length + 8) / 8;
        }

        public static void Encode(Span<byte> span, Attestation container)
        {
            if (span.Length != Ssz.AttestationLength(container)) ThrowTargetLength<Attestation>(span.Length, Ssz.AttestationLength(container));
            int offset = 0;
            int dynamicOffset = Ssz.AttestationDynamicOffset;
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
                int currentLength = Ssz.AttestationLength(containers[i]);
                Encode(span.Slice(offset, VarOffsetSize), dynamicOffset);
                Encode(span.Slice(dynamicOffset, currentLength), containers[i]);
                offset += VarOffsetSize;
                dynamicOffset += currentLength;
            }
        }

        public static Attestation[] DecodeAttestations(ReadOnlySpan<byte> span)
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

        public static Attestation DecodeAttestation(ReadOnlySpan<byte> span)
        {
            int offset = 0;

            // static part
            DecodeDynamicOffset(span, ref offset, out int dynamicOffset1);
            AttestationData data = DecodeAttestationData(span, ref offset);
            BlsSignature signature = DecodeBlsSignature(span, ref offset);

            // var part
            BitArray aggregationBits = DecodeBitlist(span.Slice(dynamicOffset1, span.Length - dynamicOffset1));

            Attestation container = new Attestation(aggregationBits, data, signature);

            return container;
        }

        private static void Encode(Span<byte> span, IReadOnlyList<Attestation> attestations, ref int offset)
        {
            // Semantics of Encode = write container into span at offset, then increase offset by the bytes written

            // Static
            int staticOffset = offset;
            int dynamicOffset = attestations.Count * VarOffsetSize;
            offset += dynamicOffset;
            foreach (Attestation attestation in attestations)
            {
                int length = Ssz.AttestationLength(attestation);
                Encode(span, dynamicOffset, ref staticOffset);
                dynamicOffset += length;
                Encode(span.Slice(offset, length), attestation);
                offset += length;
            }
        }
    }
}
