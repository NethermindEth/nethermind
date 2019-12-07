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
using Nethermind.Core2.Containers;

namespace Nethermind.Ssz
{
    public static partial class Ssz
    {
        private static BeaconBlockHeader DecodeBeaconBlockHeader(Span<byte> span, ref int offset)
        {
            BeaconBlockHeader beaconBlockHeader = DecodeBeaconBlockHeader(span.Slice(offset, BeaconBlockHeader.SszLength));
            offset += BeaconBlockHeader.SszLength;
            return beaconBlockHeader;
        }
        
        public static void Encode(Span<byte> span, BeaconBlockHeader? container)
        {
            if (container is null)
            {
                return;
            }
            
            if (span.Length != BeaconBlockHeader.SszLength) ThrowTargetLength<BeaconBlockHeader>(span.Length, BeaconBlockHeader.SszLength);
            int offset = 0;
            Encode(span, container.Slot, ref offset);
            Encode(span, container.ParentRoot, ref offset);
            Encode(span, container.StateRoot, ref offset);
            Encode(span, container.BodyRoot, ref offset);
            Encode(span, container.Signature, ref offset);
        }

        public static BeaconBlockHeader DecodeBeaconBlockHeader(Span<byte> span)
        {
            if (span.Length != BeaconBlockHeader.SszLength) ThrowSourceLength<BeaconBlockHeader>(span.Length, BeaconBlockHeader.SszLength);
            int offset = 0;
            BeaconBlockHeader container = new BeaconBlockHeader();
            container.Slot = DecodeSlot(span, ref offset);
            container.ParentRoot = DecodeSha256(span, ref offset);
            container.StateRoot = DecodeSha256(span, ref offset);
            container.BodyRoot = DecodeSha256(span, ref offset);
            container.Signature = DecodeBlsSignature(span, ref offset);
            return container;
        }
    }
}