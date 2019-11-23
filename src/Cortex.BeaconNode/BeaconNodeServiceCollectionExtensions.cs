using Cortex.BeaconNode.Configuration;
using Cortex.BeaconNode.Data;
using Cortex.Containers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Cortex.BeaconNode
{
    public static class BeaconNodeServiceCollectionExtensions
    {
        public static void AddBeaconNode(this IServiceCollection services, IConfiguration configuration)
        {
            AddConfiguration(services, configuration);

            services.AddSingleton<BeaconChain>();
            services.AddSingleton<BeaconChainUtility>();
            services.AddSingleton<BeaconStateAccessor>();
            services.AddSingleton<BeaconStateTransition>();
            services.AddSingleton<BeaconStateMutator>();
            services.AddSingleton<BeaconNodeConfiguration>();
            services.AddSingleton<IStoreProvider, StoreProvider>();
            services.AddSingleton<ICryptographyService, CryptographyService>();

            services.AddScoped<BlockProducer>();
        }

        private static void AddConfiguration(IServiceCollection services, IConfiguration configuration)
        {
            services.AddSingleton<ChainConstants>();
            services.Configure<MiscellaneousParameters>(x =>
            {
                x.MaximumCommitteesPerSlot = configuration.GetValue<ulong>("MAX_COMMITTEES_PER_SLOT");
                //x.ShardCount = new Shard(configuration.GetValue<ulong>("SHARD_COUNT"));
                x.TargetCommitteeSize = configuration.GetValue<ulong>("TARGET_COMMITTEE_SIZE");
                x.MaximumValidatorsPerCommittee = configuration.GetValue<ulong>("MAX_VALIDATORS_PER_COMMITTEE");
                x.MinimumPerEpochChurnLimit = configuration.GetValue<ulong>("MIN_PER_EPOCH_CHURN_LIMIT");
                x.ChurnLimitQuotient = configuration.GetValue<ulong>("CHURN_LIMIT_QUOTIENT");
                x.ShuffleRoundCount = configuration.GetValue<int>("SHUFFLE_ROUND_COUNT");
                x.MinimumGenesisActiveValidatorCount = configuration.GetValue<int>("MIN_GENESIS_ACTIVE_VALIDATOR_COUNT");
                x.MinimumGenesisTime = configuration.GetValue<ulong>("MIN_GENESIS_TIME");
            });
            services.Configure<GweiValues>(x =>
            {
                x.MaximumEffectiveBalance = new Gwei(configuration.GetValue<ulong>("MAX_EFFECTIVE_BALANCE"));
                x.EjectionBalance = new Gwei(configuration.GetValue<ulong>("EJECTION_BALANCE"));
                x.EffectiveBalanceIncrement = new Gwei(configuration.GetValue<ulong>("EFFECTIVE_BALANCE_INCREMENT"));
            });
            services.Configure<InitialValues>(x =>
            {
                x.GenesisEpoch = new Epoch(configuration.GetValue<ulong>("GENESIS_EPOCH"));
                x.BlsWithdrawalPrefix = configuration.GetValue<byte>("BLS_WITHDRAWAL_PREFIX");
            });
            services.Configure<TimeParameters>(x =>
            {
                x.MinimumAttestationInclusionDelay = new Slot(configuration.GetValue<ulong>("MIN_ATTESTATION_INCLUSION_DELAY"));
                x.SlotsPerEpoch = new Slot(configuration.GetValue<ulong>("SLOTS_PER_EPOCH"));
                x.MinimumSeedLookahead = new Epoch(configuration.GetValue<ulong>("MIN_SEED_LOOKAHEAD"));
                x.MaximumSeedLookahead = new Epoch(configuration.GetValue<ulong>("MAX_SEED_LOOKAHEAD"));
                x.SlotsPerEth1VotingPeriod = new Slot(configuration.GetValue<ulong>("SLOTS_PER_ETH1_VOTING_PERIOD"));
                x.SlotsPerHistoricalRoot = new Slot(configuration.GetValue<ulong>("SLOTS_PER_HISTORICAL_ROOT"));
                x.MinimumValidatorWithdrawabilityDelay = new Epoch(configuration.GetValue<ulong>("MIN_VALIDATOR_WITHDRAWABILITY_DELAY"));
                x.PersistentCommitteePeriod = new Epoch(configuration.GetValue<ulong>("PERSISTENT_COMMITTEE_PERIOD"));
                //x.MaximumEpochsPerCrosslink = new Epoch(configuration.GetValue<ulong>("MAX_EPOCHS_PER_CROSSLINK"));
                x.MinimumEpochsToInactivityPenalty = new Epoch(configuration.GetValue<ulong>("MIN_EPOCHS_TO_INACTIVITY_PENALTY"));
            });
            services.Configure<StateListLengths>(x =>
            {
                x.EpochsPerHistoricalVector = new Epoch(configuration.GetValue<ulong>("EPOCHS_PER_HISTORICAL_VECTOR"));
                x.EpochsPerSlashingsVector = new Epoch(configuration.GetValue<ulong>("EPOCHS_PER_SLASHINGS_VECTOR"));
                x.HistoricalRootsLimit = configuration.GetValue<ulong>("HISTORICAL_ROOTS_LIMIT");
                x.ValidatorRegistryLimit = configuration.GetValue<ulong>("VALIDATOR_REGISTRY_LIMIT");
            });
            services.Configure<RewardsAndPenalties>(x =>
            {
                x.BaseRewardFactor = configuration.GetValue<ulong>("BASE_REWARD_FACTOR");
                x.WhistleblowerRewardQuotient = configuration.GetValue<ulong>("WHISTLEBLOWER_REWARD_QUOTIENT");
                x.ProposerRewardQuotient = configuration.GetValue<ulong>("PROPOSER_REWARD_QUOTIENT");
                x.InactivityPenaltyQuotient = configuration.GetValue<ulong>("INACTIVITY_PENALTY_QUOTIENT");
                x.MinimumSlashingPenaltyQuotient = configuration.GetValue<ulong>("MIN_SLASHING_PENALTY_QUOTIENT");
            });
            services.Configure<MaxOperationsPerBlock>(x =>
            {
                x.MaximumProposerSlashings = configuration.GetValue<ulong>("MAX_PROPOSER_SLASHINGS");
                x.MaximumAttesterSlashings = configuration.GetValue<ulong>("MAX_ATTESTER_SLASHINGS");
                x.MaximumAttestations = configuration.GetValue<ulong>("MAX_ATTESTATIONS");
                x.MaximumDeposits = configuration.GetValue<ulong>("MAX_DEPOSITS");
                x.MaximumVoluntaryExits = configuration.GetValue<ulong>("MAX_VOLUNTARY_EXITS");
            });
        }
    }
}
