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