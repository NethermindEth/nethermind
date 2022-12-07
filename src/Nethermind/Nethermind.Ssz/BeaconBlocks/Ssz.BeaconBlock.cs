// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core2;
using Nethermind.Core2.Containers;

namespace Nethermind.Ssz
{
    public static partial class Ssz
    {
        private const int BeaconBlockDynamicOffset = Ssz.SlotLength + 2 * Ssz.RootLength + sizeof(uint);

        public static int BeaconBlockLength(BeaconBlock container)
        {
            return BeaconBlockDynamicOffset + Ssz.BeaconBlockBodyLength(container.Body);
        }

        public static BeaconBlock DecodeBeaconBlock(ReadOnlySpan<byte> span)
        {
            int offset = 0;
            return DecodeBeaconBlock(span, ref offset);
        }

        public static void Encode(Span<byte> span, BeaconBlock container)
        {
            if (span.Length != Ssz.BeaconBlockLength(container))
                ThrowTargetLength<BeaconBlock>(span.Length, Ssz.BeaconBlockLength(container));
            int offset = 0;
            Encode(span, container, ref offset);
        }

        //[MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static BeaconBlock DecodeBeaconBlock(ReadOnlySpan<byte> span, ref int offset)
        {
            var slot = DecodeSlot(span, ref offset);
            var parentRoot = DecodeRoot(span, ref offset);
            var stateRoot = DecodeRoot(span, ref offset);
            DecodeDynamicOffset(span, ref offset, out int dynamicOffset1);
            var body = DecodeBeaconBlockBody(span.Slice(dynamicOffset1));

            BeaconBlock beaconBlock = new BeaconBlock(slot, parentRoot, stateRoot, body);
            return beaconBlock;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Encode(Span<byte> span, BeaconBlock container, ref int offset)
        {
            // Semantics of Encode = write container into span at offset, then increase offset by the bytes written

            // Static
            Encode(span, container.Slot, ref offset);
            Encode(span, container.ParentRoot, ref offset);
            Encode(span, container.StateRoot, ref offset);
            Encode(span, Ssz.BeaconBlockDynamicOffset, ref offset);

            // Variable
            Encode(span, container.Body, ref offset);
        }
    }
}
