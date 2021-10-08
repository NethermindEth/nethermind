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

namespace Nethermind.Ssz
{
    public partial class Ssz
    {
        public static int DepositContractTreeDepth { get; private set; }
        private static int JustificationBitsLength;
        public static ulong MaximumDepositContracts { get; private set; }
        
        public static uint MaxValidatorsPerCommittee { get; private set; }
        
        public static uint SlotsPerEpoch { get; private set; }
        public static int SlotsPerEth1VotingPeriod { get; private set; }
        public static int SlotsPerHistoricalRoot { get; private set; }
        
        public static int EpochsPerHistoricalVector { get; private set; }
        public static int EpochsPerSlashingsVector { get; private set; }
        public static ulong HistoricalRootsLimit { get; private set; }
        public static ulong ValidatorRegistryLimit { get; private set; }
        
        public static uint MaxProposerSlashings { get; private set; }
        public static uint MaxAttesterSlashings { get; private set; }
        public static uint MaxAttestations { get; private set; }
        public static uint MaxDeposits { get; private set; }
        public static uint MaxVoluntaryExits { get; private set; }

        public static void Init(int depositContractTreeDepth,
            int justificationBitsLength,
            ulong maximumValidatorsPerCommittee,
            ulong slotsPerEpoch,
            ulong slotsPerEth1VotingPeriod,
            ulong slotsPerHistoricalRoot,
            ulong epochsPerHistoricalVector,
            ulong epochsPerSlashingsVector,
            ulong historicalRootsLimit,
            ulong validatorRegistryLimit,
            ulong maximumProposerSlashings,
            ulong maximumAttesterSlashings,
            ulong maximumAttestations,
            ulong maximumDeposits,
            ulong maximumVoluntaryExits
        )
        {
            DepositContractTreeDepth = depositContractTreeDepth;
            JustificationBitsLength = justificationBitsLength;
            MaxValidatorsPerCommittee = (uint)maximumValidatorsPerCommittee;
            SlotsPerEpoch = (uint)slotsPerEpoch;
            SlotsPerEth1VotingPeriod = (int)slotsPerEth1VotingPeriod;
            SlotsPerHistoricalRoot = (int)slotsPerHistoricalRoot;
            EpochsPerHistoricalVector = (int)epochsPerHistoricalVector;
            EpochsPerSlashingsVector = (int)epochsPerSlashingsVector;
            HistoricalRootsLimit = historicalRootsLimit;
            ValidatorRegistryLimit = validatorRegistryLimit;
            MaxProposerSlashings = (uint)maximumProposerSlashings;
            MaxAttesterSlashings = (uint)maximumAttesterSlashings;
            MaxAttestations = (uint)maximumAttestations;
            MaxDeposits = (uint)maximumDeposits;
            MaxVoluntaryExits = (uint)maximumVoluntaryExits;

            MaximumDepositContracts = (ulong) 1 << depositContractTreeDepth;
        }
    }
}