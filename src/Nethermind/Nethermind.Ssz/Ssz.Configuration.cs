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
using System;

namespace Nethermind.Ssz
{
    public partial class Ssz
    {
        public static ulong ValidatorRegistryLimit = 1_099_511_627_776;
        public static uint SlotsPerEpoch = 32;
        public static int SlotsPerEth1VotingPeriod = 1024;
        public static int SlotsPerHistoricalRoot = 8192;
        public static int EpochsPerHistoricalVector = 65536;
        public static int EpochsPerSlashingsVector = 8192;
        public static int JustificationBitsLength = 4;
        public static uint MaxValidatorsPerCommittee = 2048;
        public static uint MaxProposerSlashings { get; set; } = 16;
        public static uint MaxAttesterSlashings { get; set; } = 1;
        public static uint MaxAttestations { get; set; } = 128;
        public static uint MaxDeposits { get; set; } = 16;
        public static uint MaxVoluntaryExits { get; set; } = 16;        
        public static ulong HistoricalRootsLimit = 16_777_216;
        
        public static int ContractTreeDepth = 32;
        

    }
}