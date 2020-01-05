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

using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;

namespace Nethermind.Core2
{
    public static class ByteLength
    {
        public const int SlotLength = sizeof(ulong);
        public const int EpochLength = sizeof(ulong);
        public const int CommitteeIndexLength = sizeof(ulong);
        public const int GweiLength = sizeof(ulong);
        public const int ValidatorIndexLength = sizeof(ulong);
        public const int Hash32Length = Hash32.Length;
        public const int ForkVersionLength = ForkVersion.Length;
        public const int BlsPublicKeyLength = BlsPublicKey.Length;
        public const int BlsSignatureLength = BlsSignature.Length;
        public const int ForkLength = ByteLength.ForkVersionLength * 2 + ByteLength.EpochLength;
        public const int CheckpointLength = ByteLength.Hash32Length + ByteLength.EpochLength;
        public const int ValidatorLength = ByteLength.BlsPublicKeyLength + ByteLength.Hash32Length + ByteLength.GweiLength + 1 + 4 * ByteLength.EpochLength;
        public const int AttestationDataLength = ByteLength.SlotLength + ByteLength.CommitteeIndexLength + ByteLength.Hash32Length + 2 * ByteLength.CheckpointLength;

        public const int IndexedAttestationDynamicOffset = sizeof(uint) +
                                            ByteLength.AttestationDataLength +
                                            ByteLength.BlsSignatureLength;

        public static int IndexedAttestationLength(IndexedAttestation? value)
        {
            if (value is null)
            {
                return 0;
            }
            
            return ByteLength.IndexedAttestationDynamicOffset +
                   (value.AttestingIndices?.Count ?? 0) * ByteLength.ValidatorIndexLength;
        }

        public const int PendingAttestationDynamicOffset = sizeof(uint) +
                                                           ByteLength.AttestationDataLength +
                                                           ByteLength.SlotLength +
                                                           ByteLength.ValidatorIndexLength;

        public static int PendingAttestationLength(PendingAttestation? value)
        {
            if (value == null)
            {
                return 0;
            }
            
            // TODO: AggregationBits is a Bitlist, not Bitvector, so needs a sentinel '1' at the end, i.e. byte length is (Len+8)/8
            return ByteLength.PendingAttestationDynamicOffset + (value.AggregationBits.Length + 7) / 8;
        }

        public const int Eth1DataLength = 2 * ByteLength.Hash32Length + sizeof(ulong);
    }
}