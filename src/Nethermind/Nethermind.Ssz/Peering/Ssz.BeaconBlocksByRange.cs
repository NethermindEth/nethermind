// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.P2p;
using Nethermind.Core2.Types;

namespace Nethermind.Ssz
{
    public static partial class Ssz
    {
        public const int BeaconBlocksByRangeLength = RootLength + SlotLength + sizeof(ulong) + sizeof(ulong);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static BeaconBlocksByRange DecodeBeaconBlocksByRange(ReadOnlySpan<byte> span)
        {
            if (span.Length != BeaconBlocksByRangeLength)
                ThrowSourceLength<BeaconBlocksByRange>(span.Length, BeaconBlocksByRangeLength);
            int offset = 0;
            Root headBlockRoot = DecodeRoot(span, ref offset);
            Slot startSlot = DecodeSlot(span, ref offset);
            ulong count = DecodeULong(span, ref offset);
            ulong step = DecodeULong(span, ref offset);

            return new BeaconBlocksByRange(headBlockRoot, startSlot, count, step);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Encode(Span<byte> span, BeaconBlocksByRange container)
        {
            if (span.Length != BeaconBlocksByRangeLength)
                ThrowTargetLength<BeaconBlocksByRange>(span.Length, BeaconBlocksByRangeLength);
            int offset = 0;
            Encode(span, container.HeadBlockRoot, ref offset);
            Encode(span, container.StartSlot, ref offset);
            Encode(span, container.Count, ref offset);
            Encode(span, container.Step, ref offset);
        }
    }
}
