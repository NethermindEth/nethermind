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
using Nethermind.Core2;
using Nethermind.Core2.Containers;

namespace Nethermind.Ssz
{
    public static partial class Ssz
    {
        public static void Encode(Span<byte> span, BeaconBlock container)
        {
            if (span.Length != ByteLength.BeaconBlockLength(container)) ThrowTargetLength<BeaconBlock>(span.Length, ByteLength.BeaconBlockLength(container));
            int offset = 0;
            Encode(span, container.Slot, ref offset);
            Encode(span, container.ParentRoot, ref offset);
            Encode(span, container.StateRoot, ref offset);
            Encode(span, ByteLength.BeaconBlockDynamicOffset, ref offset);
            Encode(span, container.Signature, ref offset);
            Encode(span.Slice(offset), container.Body);
        }

        public static BeaconBlock DecodeBeaconBlock(Span<byte> span)
        {
            BeaconBlock beaconBlock = new BeaconBlock();

            int offset = 0;
            beaconBlock.Slot = DecodeSlot(span, ref offset);
            beaconBlock.ParentRoot = DecodeSha256(span, ref offset);
            beaconBlock.StateRoot = DecodeSha256(span, ref offset);
            DecodeDynamicOffset(span, ref offset, out int dynamicOffset1);
            beaconBlock.Signature = DecodeBlsSignature(span, ref offset);
            beaconBlock.Body = DecodeBeaconBlockBody(span.Slice(dynamicOffset1));
            return beaconBlock;
        }
    }
}