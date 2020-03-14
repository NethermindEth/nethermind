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
using System.Collections;
using System.Linq;
using Nethermind.Core2;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;

namespace Nethermind.Ssz
{
    public partial class Ssz
    {
        public static int BeaconStateDynamicOffset()
        {
            return sizeof(ulong) +
                   Ssz.SlotLength +
                   Ssz.ForkLength +
                   Ssz.BeaconBlockHeaderLength +
                   2 * SlotsPerHistoricalRoot * Ssz.RootLength +
                   sizeof(uint) +
                   Ssz.Eth1DataLength +
                   sizeof(uint) +
                   sizeof(ulong) +
                   2 * sizeof(uint) +
                   EpochsPerHistoricalVector * Ssz.Bytes32Length +
                   EpochsPerSlashingsVector * Ssz.GweiLength +
                   2 * sizeof(uint) +
                   ((Ssz.JustificationBitsLength + 7) / 8) +
                   3 * Ssz.CheckpointLength;
        }

        public static int BeaconStateLength(BeaconState container)
        {
            int result = BeaconStateDynamicOffset();
            result += Ssz.RootLength * (container.HistoricalRoots.Count);
            result += Ssz.ValidatorLength * (container.Validators.Count);
            result += Ssz.GweiLength * (container.Balances.Count);
            result += Ssz.Eth1DataLength * (container.Eth1DataVotes.Count);

            result += container.PreviousEpochAttestations.Count * sizeof(uint);
            for (int i = 0; i < container.PreviousEpochAttestations.Count; i++)
            {
                result += Ssz.PendingAttestationLength(container.PreviousEpochAttestations[i]);
            }

            result += container.CurrentEpochAttestations.Count * sizeof(uint);
            for (int i = 0; i < container.CurrentEpochAttestations.Count; i++)
            {
                result += Ssz.PendingAttestationLength(container.CurrentEpochAttestations[i]);
            }

            return result;
        }

        public static void Encode(Span<byte> span, BeaconState container)
        {
            if (span.Length != Ssz.BeaconStateLength(container))
            {
                ThrowTargetLength<BeaconState>(span.Length, Ssz.BeaconStateLength(container));
            }

            int offset = 0;
            int dynamicOffset = Ssz.BeaconStateDynamicOffset();

            Encode(span.Slice(offset, sizeof(ulong)), container.GenesisTime);
            offset += sizeof(ulong);
            Encode(span, container.Slot, ref offset);
            Encode(span, container.Fork, ref offset);
            Encode(span.Slice(offset, Ssz.BeaconBlockHeaderLength), container.LatestBlockHeader);
            offset += Ssz.BeaconBlockHeaderLength;
            Encode(span.Slice(offset, Ssz.SlotsPerHistoricalRoot * Ssz.RootLength), container.BlockRoots);
            offset += Ssz.SlotsPerHistoricalRoot * Ssz.RootLength;
            Encode(span.Slice(offset, Ssz.SlotsPerHistoricalRoot * Ssz.RootLength), container.StateRoots);
            offset += Ssz.SlotsPerHistoricalRoot * Ssz.RootLength;
            int length1 = container.HistoricalRoots.Count * Ssz.RootLength;
            Encode(span.Slice(offset, VarOffsetSize), dynamicOffset);
            Encode(span.Slice(dynamicOffset, length1), container.HistoricalRoots);
            dynamicOffset += length1;
            offset += VarOffsetSize;
            Encode(span, container.Eth1Data, ref offset);
            int length2 = container.Eth1DataVotes.Count * Ssz.Eth1DataLength;
            Encode(span.Slice(offset, VarOffsetSize), dynamicOffset);
            Encode(span.Slice(dynamicOffset, length2), container.Eth1DataVotes.ToArray());
            dynamicOffset += length2;
            offset += VarOffsetSize;
            Encode(span.Slice(offset, sizeof(ulong)), container.Eth1DepositIndex);
            offset += sizeof(ulong);
            int length3 = container.Validators.Count * Ssz.ValidatorLength;
            Encode(span.Slice(offset, VarOffsetSize), dynamicOffset);
            Encode(span.Slice(dynamicOffset, length3), container.Validators.ToArray());
            dynamicOffset += length3;
            offset += VarOffsetSize;
            int length4 = container.Balances.Count * Ssz.GweiLength;
            Encode(span.Slice(offset, VarOffsetSize), dynamicOffset);
            Encode(span.Slice(dynamicOffset, length4), container.Balances.ToArray());
            dynamicOffset += length4;
            offset += VarOffsetSize;
            Encode(span.Slice(offset, Ssz.EpochsPerHistoricalVector * Ssz.Bytes32Length), container.RandaoMixes);
            offset += Ssz.EpochsPerHistoricalVector * Ssz.Bytes32Length;
            Encode(span.Slice(offset, Ssz.EpochsPerSlashingsVector * Ssz.GweiLength), container.Slashings.ToArray());
            offset += Ssz.EpochsPerSlashingsVector * Ssz.GweiLength;

            int length5 = container.PreviousEpochAttestations.Count * VarOffsetSize;
            for (int i = 0; i < container.PreviousEpochAttestations.Count; i++)
            {
                length5 += Ssz.PendingAttestationLength(container.PreviousEpochAttestations[i]);
            }

            Encode(span.Slice(offset, VarOffsetSize), dynamicOffset);
            Encode(span.Slice(dynamicOffset, length5), container.PreviousEpochAttestations.ToArray());
            dynamicOffset += length5;
            offset += VarOffsetSize;

            int length6 = container.CurrentEpochAttestations.Count * VarOffsetSize;
            for (int i = 0; i < container.CurrentEpochAttestations.Count; i++)
            {
                length6 += Ssz.PendingAttestationLength(container.CurrentEpochAttestations[i]);
            }

            Encode(span.Slice(offset, VarOffsetSize), dynamicOffset);
            Encode(span.Slice(dynamicOffset, length6), container.CurrentEpochAttestations.ToArray());
            dynamicOffset += length6;
            offset += VarOffsetSize;
            Encode(span, container.JustificationBits, ref offset);
            Encode(span, container.PreviousJustifiedCheckpoint, ref offset);
            Encode(span, container.CurrentJustifiedCheckpoint, ref offset);
            Encode(span, container.FinalizedCheckpoint, ref offset);
        }

        public static BeaconState DecodeBeaconState(Span<byte> span)
        {
            int offset = 0;

            ulong genesisTime = DecodeULong(span, ref offset);
            Slot slot = DecodeSlot(span, ref offset);
            Fork fork = DecodeFork(span, ref offset);
            BeaconBlockHeader latestBlockHeader = DecodeBeaconBlockHeader(span, ref offset);

            Root[] blockRoots = DecodeRoots(span.Slice(offset, Ssz.SlotsPerHistoricalRoot * Ssz.RootLength)).ToArray();
            offset += Ssz.SlotsPerHistoricalRoot * Ssz.RootLength;
            Root[] stateRoots = DecodeRoots(span.Slice(offset, Ssz.SlotsPerHistoricalRoot * Ssz.RootLength)).ToArray();
            offset += Ssz.SlotsPerHistoricalRoot * Ssz.RootLength;

            DecodeDynamicOffset(span, ref offset, out int dynamicOffset1);
            Eth1Data eth1Data = DecodeEth1Data(span, ref offset);
            DecodeDynamicOffset(span, ref offset, out int dynamicOffset2);
            ulong eth1DepositIndex = DecodeULong(span, ref offset);
            DecodeDynamicOffset(span, ref offset, out int dynamicOffset3);
            DecodeDynamicOffset(span, ref offset, out int dynamicOffset4);
            Bytes32[] randaoMixes = DecodeBytes32s(span.Slice(offset, Ssz.EpochsPerHistoricalVector * Ssz.Bytes32Length));
            offset += Ssz.EpochsPerHistoricalVector * Ssz.RootLength;
            Gwei[] slashings = DecodeGweis(span.Slice(offset, Ssz.EpochsPerSlashingsVector * Ssz.GweiLength));
            offset += Ssz.EpochsPerSlashingsVector * Ssz.GweiLength;
            DecodeDynamicOffset(span, ref offset, out int dynamicOffset5);
            DecodeDynamicOffset(span, ref offset, out int dynamicOffset6);
            
            int justificationBitsByteLength = (Ssz.JustificationBitsLength + 7) / 8;
            BitArray justificationBits = DecodeBitvector(span.Slice(offset, justificationBitsByteLength), Ssz.JustificationBitsLength);
            offset += justificationBitsByteLength;
            
            Checkpoint previousJustifiedCheckpoint = DecodeCheckpoint(span, ref offset);
            Checkpoint currentJustifiedCheckpoint = DecodeCheckpoint(span, ref offset);
            Checkpoint finalizedCheckpoint = DecodeCheckpoint(span, ref offset);

            Root[] historicalRoots = DecodeRoots(span.Slice(dynamicOffset1, dynamicOffset2 - dynamicOffset1));
            Eth1Data[] eth1DataVotes = DecodeEth1Datas(span.Slice(dynamicOffset2, dynamicOffset3 - dynamicOffset2));
            Validator[] validators = DecodeValidators(span.Slice(dynamicOffset3, dynamicOffset4 - dynamicOffset3));
            Gwei[] balances = DecodeGweis(span.Slice(dynamicOffset4, dynamicOffset5 - dynamicOffset4));
            PendingAttestation[] previousEpochAttestations = DecodePendingAttestations(span.Slice(dynamicOffset5, dynamicOffset6 - dynamicOffset5));
            PendingAttestation[] currentEpochAttestations = DecodePendingAttestations(span.Slice(dynamicOffset6, span.Length - dynamicOffset6));

            BeaconState beaconState = new BeaconState(
                genesisTime,
                slot,
                fork,
                latestBlockHeader,
                blockRoots,
                stateRoots,
                historicalRoots,
                eth1Data,
                eth1DataVotes,
                eth1DepositIndex,
                validators,
                balances,
                randaoMixes,
                slashings,
                previousEpochAttestations,
                currentEpochAttestations,
                justificationBits,
                previousJustifiedCheckpoint,
                currentJustifiedCheckpoint,
                finalizedCheckpoint);

            return beaconState;
        }
    }
}