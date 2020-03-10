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
using Nethermind.Core2;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;

namespace Nethermind.Ssz
{
    public static partial class Ssz
    {
        public const int BeaconBlockHeaderLength = Ssz.SlotLength + 3 * Ssz.RootLength;
        
        private static BeaconBlockHeader DecodeBeaconBlockHeader(ReadOnlySpan<byte> span, ref int offset)
        {
            BeaconBlockHeader beaconBlockHeader = DecodeBeaconBlockHeader(span.Slice(offset, Ssz.BeaconBlockHeaderLength));
            offset += Ssz.BeaconBlockHeaderLength;
            return beaconBlockHeader;
        }
        
        public static void Encode(Span<byte> span, BeaconBlockHeader container)
        {
            if (span.Length != Ssz.BeaconBlockHeaderLength) ThrowTargetLength<BeaconBlockHeader>(span.Length, Ssz.BeaconBlockHeaderLength);
            int offset = 0;
            Encode(span, container.Slot, ref offset);
            Encode(span, container.ParentRoot, ref offset);
            Encode(span, container.StateRoot, ref offset);
            Encode(span, container.BodyRoot, ref offset);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Encode(Span<byte> span, BeaconBlockHeader container, ref int offset)
        {
            Encode(span.Slice(offset, Ssz.BeaconBlockHeaderLength), container);
            offset += Ssz.BeaconBlockHeaderLength;
        }

        public static BeaconBlockHeader DecodeBeaconBlockHeader(ReadOnlySpan<byte> span)
        {
            if (span.Length != Ssz.BeaconBlockHeaderLength) ThrowSourceLength<BeaconBlockHeader>(span.Length, Ssz.BeaconBlockHeaderLength);
            int offset = 0;
            Slot slot = DecodeSlot(span, ref offset);
            Root parentRoot = DecodeRoot(span, ref offset);
            Root stateRoot = DecodeRoot(span, ref offset);
            Root bodyRoot = DecodeRoot(span, ref offset);
            BeaconBlockHeader container = new BeaconBlockHeader(slot, parentRoot, stateRoot, bodyRoot);
            return container;
        }
    }
}