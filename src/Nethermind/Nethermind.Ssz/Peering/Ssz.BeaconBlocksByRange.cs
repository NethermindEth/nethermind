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