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

namespace Nethermind.BeaconNode.Test.Helpers
{
    public static class TestConfiguration
    {
        //public static void GetMinimalConfiguration(
        //    out ChainConstants chainConstants,
        //    out IOptionsMonitor<MiscellaneousParameters> miscellaneousParameterOptions,
        //    out IOptionsMonitor<GweiValues> gweiValueOptions,
        //    out IOptionsMonitor<InitialValues> initialValueOptions,
        //    out IOptionsMonitor<TimeParameters> timeParameterOptions,
        //    out IOptionsMonitor<StateListLengths> stateListLengthOptions,
        //    out IOptionsMonitor<RewardsAndPenalties> rewardsAndPenaltiesOptions,
        //    out IOptionsMonitor<MaxOperationsPerBlock> maxOperationsPerBlockOptions,
        //    out IOptionsMonitor<ForkChoiceConfiguration> forkChoiceConfigurationOptions)
        //{
        //    chainConstants = new ChainConstants();
        //    miscellaneousParameterOptions = TestOptionsMonitor.Create(new MiscellaneousParameters()
        //    {
        //        MaximumCommitteesPerSlot = 4,
        //        TargetCommitteeSize = 4,
        //        MaximumValidatorsPerCommittee = 2048,
        //        MinimumPerEpochChurnLimit = 4,
        //        ChurnLimitQuotient = 65536,
        //        ShuffleRoundCount = 10,
        //        MinimumGenesisActiveValidatorCount = 64,
        //        MinimumGenesisTime = 1578009600, // Jan 3, 2020
        //    });
        //    gweiValueOptions = TestOptionsMonitor.Create(new GweiValues()
        //    {
        //        MaximumEffectiveBalance = new Gwei(((ulong)1 << 5) * 1000 * 1000 * 1000),
        //        EjectionBalance = new Gwei(((ulong)1 << 4) * 1000 * 1000 * 1000),
        //        EffectiveBalanceIncrement = new Gwei(1000 * 1000 * 1000),
        //    });
        //    initialValueOptions = TestOptionsMonitor.Create(new InitialValues()
        //    {
        //        GenesisEpoch = new Epoch(0),
        //        BlsWithdrawalPrefix = 0x00,
        //    });
        //    timeParameterOptions = TestOptionsMonitor.Create(new TimeParameters()
        //    {
        //        SecondsPerSlot = 6,
        //        MinimumAttestationInclusionDelay = new Slot(1),
        //        SlotsPerEpoch = new Slot(8),
        //        MinimumSeedLookahead = new Epoch(1),
        //        MaximumSeedLookahead = new Epoch(4),
        //        SlotsPerEth1VotingPeriod = new Slot(16),
        //        SlotsPerHistoricalRoot = new Slot(64),
        //        MinimumValidatorWithdrawabilityDelay = new Epoch(256),
        //        PersistentCommitteePeriod = new Epoch(2048),
        //        //MaximumEpochsPerCrosslink = new Epoch(4),
        //        MinimumEpochsToInactivityPenalty = new Epoch(4),
        //    });
        //    stateListLengthOptions = TestOptionsMonitor.Create(new StateListLengths()
        //    {
        //        EpochsPerHistoricalVector = new Epoch(64),
        //        EpochsPerSlashingsVector = new Epoch(64),
        //        HistoricalRootsLimit = (ulong)1 << 24,
        //        ValidatorRegistryLimit = (ulong)1 << 40,
        //    });
        //    rewardsAndPenaltiesOptions = TestOptionsMonitor.Create(new RewardsAndPenalties()
        //    {
        //        BaseRewardFactor = 64,
        //        WhistleblowerRewardQuotient = 512,
        //        ProposerRewardQuotient = 8,
        //        InactivityPenaltyQuotient = 33554432,
        //        MinimumSlashingPenaltyQuotient = 32,
        //    });
        //    maxOperationsPerBlockOptions = TestOptionsMonitor.Create(new MaxOperationsPerBlock()
        //    {
        //        MaximumProposerSlashings = 16,
        //        MaximumAttesterSlashings = 1,
        //        MaximumAttestations = 128,
        //        MaximumDeposits = 16,
        //        MaximumVoluntaryExits = 16,
        //    });
        //    forkChoiceConfigurationOptions = TestOptionsMonitor.Create(new ForkChoiceConfiguration()
        //    {
        //        SafeSlotsToUpdateJustified = new Slot(8),
        //    });
        //}
    }
}
