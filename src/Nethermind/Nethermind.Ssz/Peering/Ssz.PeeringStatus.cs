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
        public const int PeeringStatusLength = ForkVersionLength + RootLength + EpochLength + RootLength + SlotLength;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static PeeringStatus DecodePeeringStatus(ReadOnlySpan<byte> span)
        {
            if (span.Length != PeeringStatusLength) ThrowSourceLength<PeeringStatus>(span.Length, PeeringStatusLength);
            int offset = 0;
            ForkVersion headForkVersion = DecodeForkVersion(span, ref offset);
            Root finalizedRoot = DecodeRoot(span, ref offset);
            Epoch finalizedEpoch = DecodeEpoch(span, ref offset);
            Root headRoot = DecodeRoot(span, ref offset);
            Slot headSlot = DecodeSlot(span, ref offset);

            return new PeeringStatus(headForkVersion, finalizedRoot, finalizedEpoch, headRoot, headSlot);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Encode(Span<byte> span, PeeringStatus container)
        {
            if (span.Length != PeeringStatusLength) ThrowTargetLength<PeeringStatus>(span.Length, PeeringStatusLength);
            int offset = 0;
            Encode(span, container.HeadForkVersion, ref offset);
            Encode(span, container.FinalizedRoot, ref offset);
            Encode(span, container.FinalizedEpoch, ref offset);
            Encode(span, container.HeadRoot, ref offset);
            Encode(span, container.HeadSlot, ref offset);
        }
    }
}
