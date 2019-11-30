using System;
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

            services.AddSingleton<ICryptographyService, CryptographyService>();
            services.AddSingleton<BeaconChain>();
            services.AddSingleton<BeaconChainUtility>();
            services.AddSingleton<BeaconStateAccessor>();
            services.AddSingleton<BeaconStateTransition>();
            services.AddSingleton<BeaconStateMutator>();
            services.AddSingleton<IStoreProvider, StoreProvider>();
            services.AddSingleton<ForkChoice>();

            services.AddSingleton<BeaconNodeConfiguration>();

            services.AddScoped<BlockProducer>();
        }

        private static void AddConfiguration(IServiceCollection services, IConfiguration configuration)
        {
            services.AddSingleton<ChainConstants>();
            services.Configure<MiscellaneousParameters>(x =>
            {
                configuration.Bind("BeaconChain:MiscellaneousParameters", x);
                //x.MaximumCommitteesPerSlot = configuration.GetValue<ulong>("MAX_COMMITTEES_PER_SLOT");
                ////x.ShardCount = new Shard(configuration.GetValue<ulong>("SHARD_COUNT"));
                //x.TargetCommitteeSize = configuration.GetValue<ulong>("TARGET_COMMITTEE_SIZE");
                //x.MaximumValidatorsPerCommittee = configuration.GetValue<ulong>("MAX_VALIDATORS_PER_COMMITTEE");
                //x.MinimumPerEpochChurnLimit = configuration.GetValue<ulong>("MIN_PER_EPOCH_CHURN_LIMIT");
                //x.ChurnLimitQuotient = configuration.GetValue<ulong>("CHURN_LIMIT_QUOTIENT");
                //x.ShuffleRoundCount = configuration.GetValue<int>("SHUFFLE_ROUND_COUNT");
                //x.MinimumGenesisActiveValidatorCount = configuration.GetValue<int>("MIN_GENESIS_ACTIVE_VALIDATOR_COUNT");
                //x.MinimumGenesisTime = configuration.GetValue<ulong>("MIN_GENESIS_TIME");
            });
            services.Configure<GweiValues>(x =>
            {
                configuration.Bind("BeaconChain:GweiValues", section =>
                {
                    x.MaximumEffectiveBalance = section.GetGwei("MaximumEffectiveBalance");
                    x.EjectionBalance = section.GetGwei("EjectionBalance");
                    x.EffectiveBalanceIncrement = section.GetGwei("EffectiveBalanceIncrement");
                });
                //x.
                //configuration.Bind("BeaconChain:GweiValues", x);
                //x.MaximumEffectiveBalance = new Gwei(configuration.GetValue<ulong>("MAX_EFFECTIVE_BALANCE"));
                //x.EjectionBalance = new Gwei(configuration.GetValue<ulong>("EJECTION_BALANCE"));
                //x.EffectiveBalanceIncrement = new Gwei(configuration.GetValue<ulong>("EFFECTIVE_BALANCE_INCREMENT"));
            });
            services.Configure<InitialValues>(x =>
            {
                configuration.Bind("BeaconChain:InitialValues", section =>
                {
                    var genesisSlot = section.GetValue<ulong>("GenesisSlot");
                    var slotsPerEpoch = configuration.GetValue<ulong>("BeaconChain:TimeParameters:SlotsPerEpoch");
                    x.GenesisEpoch = new Epoch(genesisSlot / slotsPerEpoch);
                    x.BlsWithdrawalPrefix = section.GetValue<byte>("BlsWithdrawalPrefix");
                });
                //x.GenesisEpoch = new Epoch(configuration.GetValue<ulong>("GENESIS_EPOCH"));
                //x.BlsWithdrawalPrefix = configuration.GetValue<byte>("BLS_WITHDRAWAL_PREFIX");
            });
            services.Configure<TimeParameters>(x =>
            {
                configuration.Bind("BeaconChain:TimeParameters", section =>
                {
                    x.SecondsPerSlot = section.GetValue<ulong>("SecondsPerSlot");
                    x.MinimumAttestationInclusionDelay = new Slot(section.GetValue<ulong>("MinimumAttestationInclusionDelay"));
                    x.SlotsPerEpoch = new Slot(section.GetValue<ulong>("SlotsPerEpoch"));
                    x.MinimumSeedLookahead = new Epoch(section.GetValue<ulong>("MinimumSeedLookahead"));
                    x.MaximumSeedLookahead = new Epoch(section.GetValue<ulong>("MaximumSeedLookahead"));
                    x.SlotsPerEth1VotingPeriod = new Slot(section.GetValue<ulong>("SlotsPerEth1VotingPeriod"));
                    x.SlotsPerHistoricalRoot = new Slot(section.GetValue<ulong>("SlotsPerHistoricalRoot"));
                    x.MinimumValidatorWithdrawabilityDelay = new Epoch(section.GetValue<ulong>("MinimumValidatorWithdrawabilityDelay"));
                    x.PersistentCommitteePeriod = new Epoch(section.GetValue<ulong>("PersistentCommitteePeriod"));
                    x.MinimumEpochsToInactivityPenalty = new Epoch(section.GetValue<ulong>("MinimumEpochsToInactivityPenalty"));
                });
                //x.SecondsPerSlot = configuration.GetValue<ulong>("SECONDS_PER_SLOT");
                //x.MinimumAttestationInclusionDelay = new Slot(configuration.GetValue<ulong>("MIN_ATTESTATION_INCLUSION_DELAY"));
                //x.SlotsPerEpoch = new Slot(configuration.GetValue<ulong>("SLOTS_PER_EPOCH"));
                //x.MinimumSeedLookahead = new Epoch(configuration.GetValue<ulong>("MIN_SEED_LOOKAHEAD"));
                //x.MaximumSeedLookahead = new Epoch(configuration.GetValue<ulong>("MAX_SEED_LOOKAHEAD"));
                //x.SlotsPerEth1VotingPeriod = new Slot(configuration.GetValue<ulong>("SLOTS_PER_ETH1_VOTING_PERIOD"));
                //x.SlotsPerHistoricalRoot = new Slot(configuration.GetValue<ulong>("SLOTS_PER_HISTORICAL_ROOT"));
                //x.MinimumValidatorWithdrawabilityDelay = new Epoch(configuration.GetValue<ulong>("MIN_VALIDATOR_WITHDRAWABILITY_DELAY"));
                //x.PersistentCommitteePeriod = new Epoch(configuration.GetValue<ulong>("PERSISTENT_COMMITTEE_PERIOD"));
                ////x.MaximumEpochsPerCrosslink = new Epoch(configuration.GetValue<ulong>("MAX_EPOCHS_PER_CROSSLINK"));
                //x.MinimumEpochsToInactivityPenalty = new Epoch(configuration.GetValue<ulong>("MIN_EPOCHS_TO_INACTIVITY_PENALTY"));
            });
            services.Configure<StateListLengths>(x =>
            {
                configuration.Bind("BeaconChain:StateListLengths", section =>
                {
                    x.EpochsPerHistoricalVector = new Epoch(section.GetValue<ulong>("EpochsPerHistoricalVector"));
                    x.EpochsPerSlashingsVector = new Epoch(section.GetValue<ulong>("EpochsPerSlashingsVector"));
                    x.HistoricalRootsLimit = section.GetValue<ulong>("HistoricalRootsLimit");
                    x.ValidatorRegistryLimit = section.GetValue<ulong>("ValidatorRegistryLimit");
                });
                //x.EpochsPerHistoricalVector = new Epoch(configuration.GetValue<ulong>("EPOCHS_PER_HISTORICAL_VECTOR"));
                //x.EpochsPerSlashingsVector = new Epoch(configuration.GetValue<ulong>("EPOCHS_PER_SLASHINGS_VECTOR"));
                //x.HistoricalRootsLimit = configuration.GetValue<ulong>("HISTORICAL_ROOTS_LIMIT");
                //x.ValidatorRegistryLimit = configuration.GetValue<ulong>("VALIDATOR_REGISTRY_LIMIT");
            });
            services.Configure<RewardsAndPenalties>(x =>
            {
                configuration.Bind("BeaconChain:RewardsAndPenalties", x);
                //x.BaseRewardFactor = configuration.GetValue<ulong>("BASE_REWARD_FACTOR");
                //x.WhistleblowerRewardQuotient = configuration.GetValue<ulong>("WHISTLEBLOWER_REWARD_QUOTIENT");
                //x.ProposerRewardQuotient = configuration.GetValue<ulong>("PROPOSER_REWARD_QUOTIENT");
                //x.InactivityPenaltyQuotient = configuration.GetValue<ulong>("INACTIVITY_PENALTY_QUOTIENT");
                //x.MinimumSlashingPenaltyQuotient = configuration.GetValue<ulong>("MIN_SLASHING_PENALTY_QUOTIENT");
            });
            services.Configure<MaxOperationsPerBlock>(x =>
            {
                configuration.Bind("BeaconChain:MaxOperationsPerBlock", x);
                //x.MaximumProposerSlashings = configuration.GetValue<ulong>("MAX_PROPOSER_SLASHINGS");
                //x.MaximumAttesterSlashings = configuration.GetValue<ulong>("MAX_ATTESTER_SLASHINGS");
                //x.MaximumAttestations = configuration.GetValue<ulong>("MAX_ATTESTATIONS");
                //x.MaximumDeposits = configuration.GetValue<ulong>("MAX_DEPOSITS");
                //x.MaximumVoluntaryExits = configuration.GetValue<ulong>("MAX_VOLUNTARY_EXITS");
            });
            services.Configure<SignatureDomains>(x =>
            {
                configuration.Bind("BeaconChain:SignatureDomains", section =>
                {
                    x.BeaconProposer = new DomainType(section.GetBytesFromPrefixedHex("DomainBeaconProposer"));
                    x.BeaconAttester = new DomainType(section.GetBytesFromPrefixedHex("DomainBeaconAttester"));
                    x.Randao = new DomainType(section.GetBytesFromPrefixedHex("DomainRandao"));
                    x.Deposit = new DomainType(section.GetBytesFromPrefixedHex("DomainDeposit"));
                    x.VoluntaryExit = new DomainType(section.GetBytesFromPrefixedHex("DomainVoluntaryExit"));
                });
                //x.BeaconProposer = new DomainType(configuration.GetBytesFromPrefixedHex("DOMAIN_BEACON_PROPOSER"));
                //x.BeaconAttester = new DomainType(configuration.GetBytesFromPrefixedHex("DOMAIN_BEACON_ATTESTER"));
                //x.Randao = new DomainType(configuration.GetBytesFromPrefixedHex("DOMAIN_RANDAO"));
                //x.Deposit = new DomainType(configuration.GetBytesFromPrefixedHex("DOMAIN_DEPOSIT"));
                //x.VoluntaryExit = new DomainType(configuration.GetBytesFromPrefixedHex("DOMAIN_VOLUNTARY_EXIT"));
            });
            services.Configure<ForkChoiceConfiguration>(x =>
            {
                x.SafeSlotsToUpdateJustified = new Slot(configuration.GetValue<ulong>("ForkChoiceConfiguration:SafeSlotsToUpdateJustified"));
                //x.SafeSlotsToUpdateJustified = new Slot(configuration.GetValue<ulong>("SAFE_SLOTS_TO_UPDATE_JUSTIFIED"));
            });
        }

        private static void Bind(this IConfiguration configuration, string key, Action<IConfiguration> bindSection)
        {
            var configurationSection = configuration.GetSection(key);
            bindSection(configurationSection);
        }

        private static byte[] GetBytesFromPrefixedHex(this IConfiguration configuration, string key)
        {
            var hex = configuration.GetValue<string>(key);
            if (string.IsNullOrWhiteSpace(hex))
            {
                return new byte[0];
            }

            var bytes = new byte[(hex.Length - 2) / 2];
            var hexIndex = 2;
            for (var byteIndex = 0; byteIndex < bytes.Length; byteIndex++)
            {
                bytes[byteIndex] = Convert.ToByte(hex.Substring(hexIndex, 2), 16);
                hexIndex += 2;
            }
            return bytes;
        }

        private static Gwei GetGwei(this IConfiguration configuration, string key)
        {
            return new Gwei(configuration.GetValue<ulong>(key));
        }
    }
}
