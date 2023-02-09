// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Linq;
using Nethermind.Core2;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;

namespace Nethermind.Ssz
{
    public static partial class Ssz
    {
        public const int IndexedAttestationDynamicOffset = sizeof(uint) + Ssz.AttestationDataLength + Ssz.BlsSignatureLength;

        public static int IndexedAttestationLength(IndexedAttestation? value)
        {
            if (value is null)
            {
                return 0;
            }

            return Ssz.IndexedAttestationDynamicOffset + (value.AttestingIndices?.Count ?? 0) * Ssz.ValidatorIndexLength;
        }

        public static void Encode(Span<byte> span, IndexedAttestation? container)
        {
            if (container is null)
            {
                return;
            }

            if (span.Length != Ssz.IndexedAttestationLength(container))
            {
                ThrowTargetLength<IndexedAttestation>(span.Length, Ssz.IndexedAttestationLength(container));
            }

            int offset = 0;
            int dynamicOffset = Ssz.IndexedAttestationDynamicOffset;
            Encode(span, container.AttestingIndices.ToArray(), ref offset, ref dynamicOffset);
            Encode(span, container.Data, ref offset);
            Encode(span, container.Signature, ref offset);
        }

        public static IndexedAttestation DecodeIndexedAttestation(ReadOnlySpan<byte> span)
        {
            int offset = 0;
            DecodeDynamicOffset(span, ref offset, out int dynamicOffset1);
            ValidatorIndex[] attestingIndices = DecodeValidatorIndexes(span.Slice(dynamicOffset1));
            AttestationData data = DecodeAttestationData(span, ref offset);
            BlsSignature signature = DecodeBlsSignature(span, ref offset);
            IndexedAttestation container = new IndexedAttestation(attestingIndices, data, signature);
            return container;
        }
    }
}
