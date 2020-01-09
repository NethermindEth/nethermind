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
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;

namespace Nethermind.Ssz
{
    public static partial class Ssz
    {
          public static void Encode(Span<byte> span, AttestationData container)
        {
            if (span.Length != ByteLength.AttestationDataLength)
            {
                ThrowTargetLength<AttestationData>(span.Length, ByteLength.AttestationDataLength);
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
            if (span.Length != ByteLength.AttestationDataLength)
            {
                ThrowSourceLength<AttestationData>(span.Length, ByteLength.AttestationDataLength);
            }
            
            int offset = 0;
            AttestationData container = new AttestationData(
                DecodeSlot(span, ref offset),
                DecodeCommitteeIndex(span, ref offset),
                DecodeSha256(span, ref offset),
                DecodeCheckpoint(span, ref offset),
                DecodeCheckpoint(span, ref offset));
            return container;
        }

        private static AttestationData DecodeAttestationData(Span<byte> span, ref int offset)
        {
            Slot slot = DecodeSlot(span, ref offset);
            CommitteeIndex index = DecodeCommitteeIndex(span, ref offset);
            Hash32 beaconBlockRoot = DecodeSha256(span, ref offset);
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
            
            Encode(span.Slice(offset, ByteLength.AttestationDataLength), value);
            offset += ByteLength.AttestationDataLength;
        }
    }
}