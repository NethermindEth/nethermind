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
using Nethermind.Core.Extensions;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;

namespace Nethermind.Core2.Containers
{
    public class BeaconState
    {
        public const int SszDynamicOffset = sizeof(ulong) +
                                            Slot.SszLength +
                                            Fork.SszLength +
                                            BeaconBlockHeader.SszLength +
                                            3 * sizeof(uint) +
                                            2 * Eth1Data.SszLength +
                                            sizeof(ulong) +
                                            2 * sizeof(uint) +
                                            Sha256.SszLength +
                                            4 * sizeof(uint) +
                                            3 * Checkpoint.SszLength;

        public static int SszLength(BeaconState container)
        {
            int result = SszDynamicOffset;
            result += Sha256.SszLength * container.BlockRoots.Length;
            result += Sha256.SszLength * container.StateRoots.Length;
            result += Sha256.SszLength * container.HistoricalRoots.Length;
            result += Validator.SszLength * container.Validators.Length;
            result += Gwei.SszLength * container.Balances.Length;
            result += Gwei.SszLength * container.Slashings.Length;
            result += container.JustificationBits.Length;

            for (int i = 0; i < container.PreviousEpochAttestations.Length; i++)
            {
                result += PendingAttestation.SszLength(container.PreviousEpochAttestations[i]);
            }

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
        public Sha256[] BlockRoots { get; } = new Sha256[Time.SlotsPerHistoricalRoot]; 
        public Sha256[] StateRoots { get; } = new Sha256[Time.SlotsPerHistoricalRoot];
        public Sha256[] HistoricalRoots { get; set; }
        public Eth1Data Eth1Data { get; set; }
        public Eth1Data EthDataVotes { get; set; }
        public ulong Eth1DepositIndex { get; set; }
        public Validator[] Validators { get; set; }
        public Gwei[] Balances { get; set; }
        public Sha256[] RandaoMixes { get; } = new Sha256[Time.EpochsPerHistoricalVector];
        public Gwei[] Slashings { get; set; }
        public PendingAttestation[] PreviousEpochAttestations { get; set; }
        public PendingAttestation[] CurrentEpochAttestations { get; set; }
        public byte[] JustificationBits { get; set; }
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
                   Equals(EthDataVotes, other.EthDataVotes) &&
                   Eth1DepositIndex == other.Eth1DepositIndex &&
                   Validators.Length == other.Validators.Length &&
                   Balances.Length == other.Balances.Length &&
                   Equals(RandaoMixes, other.RandaoMixes) &&
                   Slashings.Length == other.Slashings.Length &&
                   PreviousEpochAttestations.Length == other.PreviousEpochAttestations.Length &&
                   CurrentEpochAttestations.Length == other.CurrentEpochAttestations.Length &&
                   Bytes.AreEqual(JustificationBits, other.JustificationBits) &&
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