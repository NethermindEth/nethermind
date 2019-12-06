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
using Nethermind.Core2.Types;

namespace Nethermind.Core2.Containers
{
    public class BeaconState
    {
        public static int SszDynamicOffset = sizeof(ulong) +
                                            Slot.SszLength +
                                            Fork.SszLength +
                                            BeaconBlockHeader.SszLength +
                                            2 * Time.SlotsPerHistoricalRoot * Hash32.SszLength +
                                            sizeof(uint) +
                                            Eth1Data.SszLength +
                                            sizeof(uint) +
                                            sizeof(ulong) +
                                            2 * sizeof(uint) +
                                            Time.EpochsPerHistoricalVector * Hash32.SszLength +
                                            Time.EpochsPerSlashingsVector * Gwei.SszLength +
                                            2 * sizeof(uint) +
                                            1 + // not sure
                                            3 * Checkpoint.SszLength;

        public const ulong HistoricalRootsLimit = 16_777_216;
        
        public static int SszLength(BeaconState container)
        {
            int result = SszDynamicOffset;
            result += Hash32.SszLength * container.HistoricalRoots.Length;
            result += Validator.SszLength * container.Validators.Length;
            result += Gwei.SszLength * container.Balances.Length;
            result += Eth1Data.SszLength * container.Eth1DataVotes.Length;

            result += container.PreviousEpochAttestations.Length * sizeof(uint);
            for (int i = 0; i < container.PreviousEpochAttestations.Length; i++)
            {
                result += PendingAttestation.SszLength(container.PreviousEpochAttestations[i]);
            }

            result += container.CurrentEpochAttestations.Length * sizeof(uint);
            for (int i = 0; i < container.CurrentEpochAttestations.Length; i++)
            {
                result += PendingAttestation.SszLength(container.CurrentEpochAttestations[i]);
            }

            return result;
        }

        public ulong GenesisTime { get; set; }
        public Slot Slot { get; set; }
        public Fork Fork { get; set; }
        public BeaconBlockHeader LatestBlockHeader { get; set; }
        public Hash32[] BlockRoots { get; set; } = new Hash32[Time.SlotsPerHistoricalRoot]; 
        public Hash32[] StateRoots { get; set; } = new Hash32[Time.SlotsPerHistoricalRoot];
        public Hash32[] HistoricalRoots { get; set; }
        public Eth1Data Eth1Data { get; set; }
        public Eth1Data[] Eth1DataVotes { get; set; }
        public ulong Eth1DepositIndex { get; set; }
        public Validator[] Validators { get; set; }
        public Gwei[] Balances { get; set; }
        public Hash32[] RandaoMixes { get; set; } = new Hash32[Time.EpochsPerHistoricalVector];
        public Gwei[] Slashings { get; set; } = new Gwei[Time.EpochsPerSlashingsVector];
        public PendingAttestation[] PreviousEpochAttestations { get; set; }
        public PendingAttestation[] CurrentEpochAttestations { get; set; }
        public byte JustificationBits { get; set; }
        public Checkpoint PreviousJustifiedCheckpoint { get; set; }
        public Checkpoint CurrentJustifiedCheckpoint { get; set; }
        public Checkpoint FinalizedCheckpoint { get; set; }
        
        public bool Equals(BeaconState other)
        {
            return GenesisTime == other.GenesisTime &&
                   Slot.Equals(other.Slot) &&
                   Fork.Equals(other.Fork) &&
                   Equals(LatestBlockHeader, other.LatestBlockHeader) &&
                   BlockRoots.Length == other.BlockRoots.Length &&
                   StateRoots.Length == other.StateRoots.Length &&
                   HistoricalRoots.Length == other.HistoricalRoots.Length &&
                   Equals(Eth1Data, other.Eth1Data) &&
                   Eth1DataVotes.Length == other.Eth1DataVotes.Length &&
                   Eth1DepositIndex == other.Eth1DepositIndex &&
                   Validators.Length == other.Validators.Length &&
                   Balances.Length == other.Balances.Length &&
                   RandaoMixes.Length == other.RandaoMixes.Length &&
                   Slashings.Length == other.Slashings.Length &&
                   PreviousEpochAttestations.Length == other.PreviousEpochAttestations.Length &&
                   CurrentEpochAttestations.Length == other.CurrentEpochAttestations.Length &&
                   JustificationBits == other.JustificationBits &&
                   PreviousJustifiedCheckpoint.Equals(other.PreviousJustifiedCheckpoint) &&
                   CurrentJustifiedCheckpoint.Equals(other.CurrentJustifiedCheckpoint) &&
                   FinalizedCheckpoint.Equals(other.FinalizedCheckpoint);
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            return obj.GetType() == GetType() && Equals((BeaconState) obj);
        }

        public override int GetHashCode()
        {
            throw new NotSupportedException();
        }
    }
}