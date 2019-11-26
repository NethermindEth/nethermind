using System.Collections.Generic;
using Cortex.BeaconNode.Configuration;
using Cortex.Containers;
using Microsoft.Extensions.Options;

namespace Cortex.BeaconNode.Tests.Helpers
{
    public static class TestConfiguration
    {
        public static IDictionary<string, string> GetMinimalConfigurationDictionary()
        {
            var configuration = new Dictionary<string, string>
            {
                // Miscellaneous parameters
                ["MAX_COMMITTEES_PER_SLOT"] = "4",
                ["TARGET_COMMITTEE_SIZE"] = "4",
                ["MAX_VALIDATORS_PER_COMMITTEE"] = "2048",
                ["MIN_PER_EPOCH_CHURN_LIMIT"] = "4",
                ["CHURN_LIMIT_QUOTIENT"] = "65536",
                ["SHUFFLE_ROUND_COUNT"] = "10",
                ["MIN_GENESIS_ACTIVE_VALIDATOR_COUNT"] = "64",
                ["MIN_GENESIS_TIME"] = "1578009600", // Jan 3, 2020

                // Gwei values
                //["MIN_DEPOSIT_AMOUNT"] = "",
                ["MAX_EFFECTIVE_BALANCE"] = "32000000000",
                ["EJECTION_BALANCE"] = "16000000000",
                ["EFFECTIVE_BALANCE_INCREMENT"] = "1000000000",

                // Initial values
                //["GENESIS_SLOT"] = "0",
                ["GENESIS_EPOCH"] = "0",
                ["BLS_WITHDRAWAL_PREFIX"] = "0x00",

                // Time parameters
                ["SECONDS_PER_SLOT"] = "6",
                ["MIN_ATTESTATION_INCLUSION_DELAY"] = "1",
                ["SLOTS_PER_EPOCH"] = "8",
                ["MIN_SEED_LOOKAHEAD"] = "1",
                ["MAX_SEED_LOOKAHEAD"] = "4",
                ["SLOTS_PER_ETH1_VOTING_PERIOD"] = "16",
                ["SLOTS_PER_HISTORICAL_ROOT"] = "64",
                ["MIN_VALIDATOR_WITHDRAWABILITY_DELAY"] = "256",
                ["PERSISTENT_COMMITTEE_PERIOD"] = "2048",
                ["MIN_EPOCHS_TO_INACTIVITY_PENALTY"] = "4",

                // State list lengths
                ["EPOCHS_PER_HISTORICAL_VECTOR"] = "64",
                ["EPOCHS_PER_SLASHINGS_VECTOR	"] = "64",
                ["HISTORICAL_ROOTS_LIMIT"] = "16777216",
                ["VALIDATOR_REGISTRY_LIMIT"] = "1099511627776",

                // Reward and penalty quotients
                ["BASE_REWARD_FACTOR"] = "64",
                ["WHISTLEBLOWER_REWARD_QUOTIENT"] = "512",
                ["PROPOSER_REWARD_QUOTIENT"] = "8",
                ["INACTIVITY_PENALTY_QUOTIENT"] = "33554432",
                ["MIN_SLASHING_PENALTY_QUOTIENT"] = "32",

                // Max operations per block
                ["MAX_PROPOSER_SLASHINGS"] = "16",
                ["MAX_ATTESTER_SLASHINGS"] = "1",
                ["MAX_ATTESTATIONS"] = "128",
                ["MAX_DEPOSITS"] = "16",
                ["MAX_VOLUNTARY_EXITS"] = "16",

                // Fork choice configuration
                ["SAFE_SLOTS_TO_UPDATE_JUSTIFIED"] = "8",
            };
            return configuration;
        }

        public static void GetMinimalConfiguration(
            out ChainConstants chainConstants,
            out IOptionsMonitor<MiscellaneousParameters> miscellaneousParameterOptions,
            out IOptionsMonitor<GweiValues> gweiValueOptions,
            out IOptionsMonitor<InitialValues> initialValueOptions,
            out IOptionsMonitor<TimeParameters> timeParameterOptions,
            out IOptionsMonitor<StateListLengths> stateListLengthOptions,
            out IOptionsMonitor<RewardsAndPenalties> rewardsAndPenaltiesOptions,
            out IOptionsMonitor<MaxOperationsPerBlock> maxOperationsPerBlockOptions,
            out IOptionsMonitor<ForkChoiceConfiguration> forkChoiceConfigurationOptions)
        {
            chainConstants = new ChainConstants();
            miscellaneousParameterOptions = TestOptionsMonitor.Create(new MiscellaneousParameters()
            {
                MaximumCommitteesPerSlot = 4,
                TargetCommitteeSize = 4,
                MaximumValidatorsPerCommittee = 2048,
                MinimumPerEpochChurnLimit = 4,
                ChurnLimitQuotient = 65536,
                ShuffleRoundCount = 10,
                MinimumGenesisActiveValidatorCount = 64,
                MinimumGenesisTime = 1578009600, // Jan 3, 2020
            });
            gweiValueOptions = TestOptionsMonitor.Create(new GweiValues()
            {
                MaximumEffectiveBalance = new Gwei(((ulong)1 << 5) * 1000 * 1000 * 1000),
                EjectionBalance = new Gwei(((ulong)1 << 4) * 1000 * 1000 * 1000),
                EffectiveBalanceIncrement = new Gwei(1000 * 1000 * 1000),
            });
            initialValueOptions = TestOptionsMonitor.Create(new InitialValues()
            {
                GenesisEpoch = new Epoch(0),
                BlsWithdrawalPrefix = 0x00,
            });
            timeParameterOptions = TestOptionsMonitor.Create(new TimeParameters()
            {
                SecondsPerSlot = 6,
                MinimumAttestationInclusionDelay = new Slot(1),
                SlotsPerEpoch = new Slot(8),
                MinimumSeedLookahead = new Epoch(1),
                MaximumSeedLookahead = new Epoch(4),
                SlotsPerEth1VotingPeriod = new Slot(16),
                SlotsPerHistoricalRoot = new Slot(64),
                MinimumValidatorWithdrawabilityDelay = new Epoch(256),
                PersistentCommitteePeriod = new Epoch(2048),
                //MaximumEpochsPerCrosslink = new Epoch(4),
                MinimumEpochsToInactivityPenalty = new Epoch(4),
            });
            stateListLengthOptions = TestOptionsMonitor.Create(new StateListLengths()
            {
                EpochsPerHistoricalVector = new Epoch(64),
                EpochsPerSlashingsVector = new Epoch(64),
                HistoricalRootsLimit = (ulong)1 << 24,
                ValidatorRegistryLimit = (ulong)1 << 40,
            });
            rewardsAndPenaltiesOptions = TestOptionsMonitor.Create(new RewardsAndPenalties()
            {
                BaseRewardFactor = 64,
                WhistleblowerRewardQuotient = 512,
                ProposerRewardQuotient = 8,
                InactivityPenaltyQuotient = 33554432,
                MinimumSlashingPenaltyQuotient = 32,
            });
            maxOperationsPerBlockOptions = TestOptionsMonitor.Create(new MaxOperationsPerBlock()
            {
                MaximumProposerSlashings = 16,
                MaximumAttesterSlashings = 1,
                MaximumAttestations = 128,
                MaximumDeposits = 16,
                MaximumVoluntaryExits = 16,
            });
            forkChoiceConfigurationOptions = TestOptionsMonitor.Create(new ForkChoiceConfiguration()
            {
                SafeSlotsToUpdateJustified = new Slot(8),
            });
        }
    }
}
