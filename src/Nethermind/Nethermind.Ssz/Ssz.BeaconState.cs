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
using System.Linq;
using Nethermind.Core2;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Types;

namespace Nethermind.Ssz
{
    public partial class Ssz
    {
        public static void Encode(Span<byte> span, BeaconState? container)
        {
            if (container is null)
            {
                return;
            }
            
            if (span.Length != ByteLength.BeaconStateLength(container))
            {
                ThrowTargetLength<BeaconState>(span.Length, ByteLength.BeaconStateLength(container));
            }

            int offset = 0;
            int dynamicOffset = ByteLength.BeaconStateDynamicOffset;

            Encode(span.Slice(offset, sizeof(ulong)), container.GenesisTime);
            offset += sizeof(ulong);
            Encode(span, container.Slot, ref offset);
            Encode(span, container.Fork, ref offset);
            Encode(span.Slice(offset, ByteLength.BeaconBlockHeaderLength), container.LatestBlockHeader);
            offset += ByteLength.BeaconBlockHeaderLength;
            Encode(span.Slice(offset, Time.SlotsPerHistoricalRoot * ByteLength.Hash32Length), container.BlockRoots);
            offset += Time.SlotsPerHistoricalRoot * ByteLength.Hash32Length;
            Encode(span.Slice(offset, Time.SlotsPerHistoricalRoot * ByteLength.Hash32Length), container.StateRoots);
            offset += Time.SlotsPerHistoricalRoot * ByteLength.Hash32Length;
            int length1 = (container.HistoricalRoots?.Length ?? 0) * ByteLength.Hash32Length;
            Encode(span.Slice(offset, VarOffsetSize), dynamicOffset);
            Encode(span.Slice(dynamicOffset, length1), container.HistoricalRoots);
            dynamicOffset += length1;
            offset += VarOffsetSize;
            Encode(span, container.Eth1Data, ref offset);
            int length2 = (container.Eth1DataVotes?.Length ?? 0) * ByteLength.Eth1DataLength;
            Encode(span.Slice(offset, VarOffsetSize), dynamicOffset);
            Encode(span.Slice(dynamicOffset, length2), container.Eth1DataVotes);
            dynamicOffset += length2;
            offset += VarOffsetSize;
            Encode(span.Slice(offset, sizeof(ulong)), container.Eth1DepositIndex);
            offset += sizeof(ulong);
            int length3 = (container.Validators?.Length ?? 0) * ByteLength.ValidatorLength;
            Encode(span.Slice(offset, VarOffsetSize), dynamicOffset);
            Encode(span.Slice(dynamicOffset, length3), container.Validators);
            dynamicOffset += length3;
            offset += VarOffsetSize;
            int length4 = (container.Balances?.Length ?? 0) * ByteLength.GweiLength;
            Encode(span.Slice(offset, VarOffsetSize), dynamicOffset);
            Encode(span.Slice(dynamicOffset, length4), container.Balances);
            dynamicOffset += length4;
            offset += VarOffsetSize;
            Encode(span.Slice(offset, Time.EpochsPerHistoricalVector * ByteLength.Hash32Length), container.RandaoMixes);
            offset += Time.EpochsPerHistoricalVector * ByteLength.Hash32Length;
            Encode(span.Slice(offset, Time.EpochsPerSlashingsVector * ByteLength.GweiLength), container.Slashings);
            offset += Time.EpochsPerSlashingsVector * ByteLength.GweiLength;

            int length5 = (container.PreviousEpochAttestations?.Length ?? 0) * VarOffsetSize;
            if (!(container.PreviousEpochAttestations is null))
            {
                for (int i = 0; i < container.PreviousEpochAttestations.Length; i++)
                {
                    length5 += ByteLength.PendingAttestationLength(container.PreviousEpochAttestations[i]);
                }
            }

            Encode(span.Slice(offset, VarOffsetSize), dynamicOffset);
            Encode(span.Slice(dynamicOffset, length5), container.PreviousEpochAttestations);
            dynamicOffset += length5;
            offset += VarOffsetSize;

            int length6 = (container.CurrentEpochAttestations?.Length ?? 0) * VarOffsetSize;
            if (!(container.CurrentEpochAttestations is null))
            {
                for (int i = 0; i < container.CurrentEpochAttestations.Length; i++)
                {
                    length6 += ByteLength.PendingAttestationLength(container.CurrentEpochAttestations[i]);
                }
            }

            Encode(span.Slice(offset, VarOffsetSize), dynamicOffset);
            Encode(span.Slice(dynamicOffset, length6), container.CurrentEpochAttestations);
            dynamicOffset += length6;
            offset += VarOffsetSize;

            Encode(span.Slice(offset, 1), container.JustificationBits);
            offset += 1;
            Encode(span, container.PreviousJustifiedCheckpoint, ref offset);
            Encode(span, container.CurrentJustifiedCheckpoint, ref offset);
            Encode(span, container.FinalizedCheckpoint, ref offset);
        }

        public static BeaconState DecodeBeaconState(Span<byte> span)
        {
            int offset = 0;
            BeaconState beaconState = new BeaconState();
            beaconState.GenesisTime = DecodeULong(span, ref offset);
            beaconState.Slot = DecodeSlot(span, ref offset);
            beaconState.Fork = DecodeFork(span, ref offset);
            beaconState.LatestBlockHeader = DecodeBeaconBlockHeader(span, ref offset);

            beaconState.BlockRoots = DecodeHashes(span.Slice(offset, Time.SlotsPerHistoricalRoot * ByteLength.Hash32Length)).ToArray();
            offset += Time.SlotsPerHistoricalRoot * ByteLength.Hash32Length;
            beaconState.StateRoots = DecodeHashes(span.Slice(offset, Time.SlotsPerHistoricalRoot * ByteLength.Hash32Length)).ToArray();
            offset += Time.SlotsPerHistoricalRoot * ByteLength.Hash32Length;

            DecodeDynamicOffset(span, ref offset, out int dynamicOffset1);
            beaconState.Eth1Data = DecodeEth1Data(span, ref offset);
            DecodeDynamicOffset(span, ref offset, out int dynamicOffset2);
            beaconState.Eth1DepositIndex = DecodeULong(span, ref offset);
            DecodeDynamicOffset(span, ref offset, out int dynamicOffset3);
            DecodeDynamicOffset(span, ref offset, out int dynamicOffset4);
            beaconState.RandaoMixes = DecodeHashes(span.Slice(offset, Time.EpochsPerHistoricalVector * ByteLength.Hash32Length));
            offset += Time.EpochsPerHistoricalVector * ByteLength.Hash32Length;
            beaconState.Slashings = DecodeGweis(span.Slice(offset, Time.EpochsPerSlashingsVector * ByteLength.GweiLength));
            offset += Time.EpochsPerSlashingsVector * ByteLength.GweiLength;
            DecodeDynamicOffset(span, ref offset, out int dynamicOffset5);
            DecodeDynamicOffset(span, ref offset, out int dynamicOffset6);

            // how many justification bits?
            beaconState.JustificationBits = DecodeByte(span.Slice(offset, 1));
            offset += 1;

            beaconState.PreviousJustifiedCheckpoint = DecodeCheckpoint(span, ref offset);
            beaconState.CurrentJustifiedCheckpoint = DecodeCheckpoint(span, ref offset);
            beaconState.FinalizedCheckpoint = DecodeCheckpoint(span, ref offset);

            beaconState.HistoricalRoots = DecodeHashes(span.Slice(dynamicOffset1, dynamicOffset2 - dynamicOffset1));
            beaconState.Eth1DataVotes = DecodeEth1Datas(span.Slice(dynamicOffset2, dynamicOffset3 - dynamicOffset2));
            beaconState.Validators = DecodeValidators(span.Slice(dynamicOffset3, dynamicOffset4 - dynamicOffset3));
            beaconState.Balances = DecodeGweis(span.Slice(dynamicOffset4, dynamicOffset5 - dynamicOffset4));
            beaconState.PreviousEpochAttestations = DecodePendingAttestations(span.Slice(dynamicOffset5, dynamicOffset6 - dynamicOffset5));
            beaconState.CurrentEpochAttestations = DecodePendingAttestations(span.Slice(dynamicOffset6, span.Length - dynamicOffset6));

            return beaconState;
        }
    }
}