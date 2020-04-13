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