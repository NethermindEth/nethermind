// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core2;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;

namespace Nethermind.Ssz
{
    public static partial class Ssz
    {
        public const int AttestationDataLength = Ssz.SlotLength + Ssz.CommitteeIndexLength + Ssz.RootLength + 2 * Ssz.CheckpointLength;

        public static void Encode(Span<byte> span, AttestationData container)
        {
            if (span.Length != Ssz.AttestationDataLength)
            {
                ThrowTargetLength<AttestationData>(span.Length, Ssz.AttestationDataLength);
            }

            int offset = 0;
            Encode(span, container.Slot, ref offset);
            Encode(span, container.Index, ref offset);
            Encode(span, container.BeaconBlockRoot, ref offset);
            Encode(span, container.Source, ref offset);
            Encode(span, container.Target, ref offset);
        }

        public static AttestationData DecodeAttestationData(Span<byte> span)
        {
            if (span.Length != Ssz.AttestationDataLength)
            {
                ThrowSourceLength<AttestationData>(span.Length, Ssz.AttestationDataLength);
            }

            int offset = 0;
            AttestationData container = new AttestationData(
                DecodeSlot(span, ref offset),
                DecodeCommitteeIndex(span, ref offset),
                DecodeRoot(span, ref offset),
                DecodeCheckpoint(span, ref offset),
                DecodeCheckpoint(span, ref offset));
            return container;
        }

        private static AttestationData DecodeAttestationData(ReadOnlySpan<byte> span, ref int offset)
        {
            Slot slot = DecodeSlot(span, ref offset);
            CommitteeIndex index = DecodeCommitteeIndex(span, ref offset);
            Root beaconBlockRoot = DecodeRoot(span, ref offset);
            Checkpoint source = DecodeCheckpoint(span, ref offset);
            Checkpoint target = DecodeCheckpoint(span, ref offset);
            AttestationData container = new AttestationData(slot, index, beaconBlockRoot, source, target);
            return container;
        }

        private static void Encode(Span<byte> span, AttestationData? value, ref int offset)
        {
            if (value is null)
            {
                return;
            }

            Encode(span.Slice(offset, Ssz.AttestationDataLength), value);
            offset += Ssz.AttestationDataLength;
        }
    }
}
