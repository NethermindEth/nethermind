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
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;

namespace Nethermind.Ssz
{
    public static partial class Ssz
    {
          public static void Encode(Span<byte> span, AttestationData container)
        {
            if (span.Length != AttestationData.SszLength)
            {
                ThrowTargetLength<AttestationData>(span.Length, AttestationData.SszLength);
            }

            int offset = 0;
            Encode(span, container.Slot, ref offset);
            Encode(span, container.CommitteeIndex, ref offset);
            Encode(span, container.BeaconBlockRoot, ref offset);
            Encode(span, container.Source, ref offset);
            Encode(span, container.Target, ref offset);
        }

        public static AttestationData DecodeAttestationData(Span<byte> span)
        {
            if (span.Length != AttestationData.SszLength)
            {
                ThrowSourceLength<AttestationData>(span.Length, AttestationData.SszLength);
            }
            
            int offset = 0;
            AttestationData container = new AttestationData();
            container.Slot = DecodeSlot(span, ref offset);
            container.CommitteeIndex = DecodeCommitteeIndex(span, ref offset);
            container.BeaconBlockRoot = DecodeSha256(span, ref offset);
            container.Source = DecodeCheckpoint(span, ref offset);
            container.Target = DecodeCheckpoint(span, ref offset);
            return container;
        }

        private static AttestationData DecodeAttestationData(Span<byte> span, ref int offset)
        {
            AttestationData container = new AttestationData();
            container.Slot = DecodeSlot(span, ref offset);
            container.CommitteeIndex = DecodeCommitteeIndex(span, ref offset);
            container.BeaconBlockRoot = DecodeSha256(span, ref offset);
            container.Source = DecodeCheckpoint(span, ref offset);
            container.Target = DecodeCheckpoint(span, ref offset);
            return container;
        }

        public static void Encode(Span<byte> span, AttestationDataAndCustodyBit container)
        {
            if (span.Length != AttestationDataAndCustodyBit.SszLength)
            {
                ThrowTargetLength<AttestationDataAndCustodyBit>(span.Length, AttestationDataAndCustodyBit.SszLength);
            }

            int offset = 0;
            Encode(span.Slice(0, AttestationData.SszLength), container.Data);
            offset += AttestationData.SszLength;
            Encode(span.Slice(offset, 1), container.CustodyBit);
        }

        public static AttestationDataAndCustodyBit DecodeAttestationDataAndCustodyBit(Span<byte> span)
        {
            if (span.Length != AttestationDataAndCustodyBit.SszLength)
            {
                ThrowSourceLength<AttestationDataAndCustodyBit>(span.Length, AttestationDataAndCustodyBit.SszLength);
            }

            AttestationDataAndCustodyBit container = new AttestationDataAndCustodyBit();
            int offset = 0;
            container.Data = DecodeAttestationData(span.Slice(offset, AttestationData.SszLength));
            offset += AttestationData.SszLength;
            container.CustodyBit = DecodeBool(span.Slice(offset, 1));
            return container;
        }

        private static void Encode(Span<byte> span, AttestationData value, ref int offset)
        {
            Encode(span.Slice(offset, AttestationData.SszLength), value);
            offset += AttestationData.SszLength;
        }
    }
}