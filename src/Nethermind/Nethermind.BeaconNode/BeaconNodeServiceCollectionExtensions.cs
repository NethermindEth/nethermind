using System.Buffers.Binary;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nethermind.BeaconNode.Configuration;
using Nethermind.BeaconNode.Services;
using Nethermind.Core2.Types;

namespace Nethermind.BeaconNode
{
    public static class BeaconNodeServiceCollectionExtensions
    {
        public static void AddBeaconNode(this IServiceCollection services, IConfiguration configuration)
        {
            AddConfiguration(services, configuration);

            services.AddSingleton<IClock, SystemClock>();
            services.AddSingleton<ICryptographyService, CortexCryptographyService>();
            services.AddSingleton<INodeStart, NodeStart>();
            services.AddSingleton<Genesis>();
            services.AddSingleton<BeaconChainUtility>();
            services.AddSingleton<BeaconStateAccessor>();
            services.AddSingleton<BeaconStateTransition>();
            services.AddSingleton<BeaconStateMutator>();
            services.AddSingleton<ForkChoice>();
            services.AddSingleton<BeaconNodeConfiguration>();

            services.AddScoped<BlockProducer>();
        }

        private static void AddConfiguration(IServiceCollection services, IConfiguration configuration)
        {
            services.AddSingleton<ChainConstants>();
            services.Configure<MiscellaneousParameters>(x =>
            {
                configuration.Bind("BeaconChain:MiscellaneousParameters", section =>
                {
                    x.MaximumCommitteesPerSlot = section.GetValue(nameof(x.MaximumCommitteesPerSlot),
                        () => configuration.GetValue<ulong>("MAX_COMMITTEES_PER_SLOT"));
                    x.TargetCommitteeSize = section.GetValue(nameof(x.TargetCommitteeSize),
                        () => configuration.GetValue<ulong>("TARGET_COMMITTEE_SIZE"));
                    x.MaximumValidatorsPerCommittee = section.GetValue(nameof(x.MaximumValidatorsPerCommittee),
                        () => configuration.GetValue<ulong>("MAX_VALIDATORS_PER_COMMITTEE"));
                    x.MinimumPerEpochChurnLimit = section.GetValue(nameof(x.MinimumPerEpochChurnLimit),
                        () => configuration.GetValue<ulong>("MIN_PER_EPOCH_CHURN_LIMIT"));
                    x.ChurnLimitQuotient = section.GetValue(nameof(x.ChurnLimitQuotient),
                        () => configuration.GetValue<ulong>("CHURN_LIMIT_QUOTIENT"));
                    x.ShuffleRoundCount = section.GetValue(nameof(x.ShuffleRoundCount),
                        () => configuration.GetValue<int>("SHUFFLE_ROUND_COUNT"));
                    x.MinimumGenesisActiveValidatorCount = section.GetValue(nameof(x.MinimumGenesisActiveValidatorCount),
                        () => configuration.GetValue<int>("MIN_GENESIS_ACTIVE_VALIDATOR_COUNT"));
                    x.MinimumGenesisTime = section.GetValue(nameof(x.MinimumGenesisTime),
                        () => configuration.GetValue<ulong>("MIN_GENESIS_TIME"));
                });
            });
            services.Configure<GweiValues>(x =>
            {
                configuration.Bind("BeaconChain:GweiValues", section =>
                {
                    x.MaximumEffectiveBalance = new Gwei(
                        section.GetValue("MaximumEffectiveBalance",
                            () => configuration.GetValue<ulong>("MAX_EFFECTIVE_BALANCE")));
                    x.EjectionBalance = new Gwei(
                        section.GetValue("EjectionBalance",
                            () => configuration.GetValue<ulong>("EJECTION_BALANCE")));
                    x.EffectiveBalanceIncrement = new Gwei(
                        section.GetValue("EffectiveBalanceIncrement",
                            () => configuration.GetValue<ulong>("EFFECTIVE_BALANCE_INCREMENT")));
                });
            });
            services.Configure<InitialValues>(x =>
            {
                configuration.Bind("BeaconChain:InitialValues", section =>
                {
                    var slotsPerEpoch = configuration.GetValue<ulong>("BeaconChain:TimeParameters:SlotsPerEpoch",
                        () => configuration.GetValue<ulong>("SLOTS_PER_EPOCH"));
                    if (slotsPerEpoch != 0)
                    {
                        var genesisSlot = section.GetValue<ulong>("GenesisSlot",
                            () => configuration.GetValue<ulong>("GENESIS_EPOCH"));
                        x.GenesisEpoch = new Epoch(genesisSlot / slotsPerEpoch);
                    }
                    x.BlsWithdrawalPrefix = section.GetValue<byte>("BlsWithdrawalPrefix",
                        () => configuration.GetValue<byte>("BLS_WITHDRAWAL_PREFIX"));
                });
            });
            services.Configure<TimeParameters>(x =>
            {
                configuration.Bind("BeaconChain:TimeParameters", section =>
                {
                    x.SecondsPerSlot = section.GetValue("SecondsPerSlot",
                        () => configuration.GetValue<uint>("SECONDS_PER_SLOT"));
                    x.MinimumAttestationInclusionDelay = new Slot(
                        section.GetValue("MinimumAttestationInclusionDelay",
                            () => configuration.GetValue<ulong>("MIN_ATTESTATION_INCLUSION_DELAY")));
                    x.SlotsPerEpoch = 
                        section.GetValue("SlotsPerEpoch",
                            () => configuration.GetValue<uint>("SLOTS_PER_EPOCH"));
                    x.MinimumSeedLookahead = new Epoch(
                        section.GetValue("MinimumSeedLookahead",
                            () => configuration.GetValue<ulong>("MIN_SEED_LOOKAHEAD")));
                    x.MaximumSeedLookahead = new Epoch(
                        section.GetValue("MaximumSeedLookahead",
                            () => configuration.GetValue<ulong>("MAX_SEED_LOOKAHEAD")));
                    x.SlotsPerEth1VotingPeriod = 
                        section.GetValue("SlotsPerEth1VotingPeriod",
                            () => configuration.GetValue<uint>("SLOTS_PER_ETH1_VOTING_PERIOD"));
                    x.SlotsPerHistoricalRoot = 
                        section.GetValue("SlotsPerHistoricalRoot",
                            () => configuration.GetValue<uint>("SLOTS_PER_HISTORICAL_ROOT"));
                    x.MinimumValidatorWithdrawabilityDelay = new Epoch(
                        section.GetValue("MinimumValidatorWithdrawabilityDelay",
                            () => configuration.GetValue<ulong>("MIN_VALIDATOR_WITHDRAWABILITY_DELAY")));
                    x.PersistentCommitteePeriod = new Epoch(
                        section.GetValue("PersistentCommitteePeriod",
                            () => configuration.GetValue<ulong>("PERSISTENT_COMMITTEE_PERIOD")));
                    x.MinimumEpochsToInactivityPenalty = new Epoch(
                        section.GetValue("MinimumEpochsToInactivityPenalty",
                            () => configuration.GetValue<ulong>("MIN_EPOCHS_TO_INACTIVITY_PENALTY")));
                });
            });
            services.Configure<StateListLengths>(x =>
            {
                configuration.Bind("BeaconChain:StateListLengths", section =>
                {
                    x.EpochsPerHistoricalVector = 
                        section.GetValue("EpochsPerHistoricalVector",
                            () => configuration.GetValue<uint>("EPOCHS_PER_HISTORICAL_VECTOR"));
                    x.EpochsPerSlashingsVector = 
                        section.GetValue("EpochsPerSlashingsVector",
                            () => configuration.GetValue<uint>("EPOCHS_PER_SLASHINGS_VECTOR"));
                    x.HistoricalRootsLimit = section.GetValue("HistoricalRootsLimit",
                        () => configuration.GetValue<ulong>("HISTORICAL_ROOTS_LIMIT"));
                    x.ValidatorRegistryLimit = section.GetValue("ValidatorRegistryLimit",
                        () => configuration.GetValue<ulong>("VALIDATOR_REGISTRY_LIMIT"));
                });
            });
            services.Configure<RewardsAndPenalties>(x =>
            {
                configuration.Bind("BeaconChain:RewardsAndPenalties", section =>
                {
                    x.BaseRewardFactor = section.GetValue(nameof(x.BaseRewardFactor),
                        () => configuration.GetValue<ulong>("BASE_REWARD_FACTOR"));
                    x.WhistleblowerRewardQuotient = section.GetValue(nameof(x.WhistleblowerRewardQuotient),
                        () => configuration.GetValue<ulong>("WHISTLEBLOWER_REWARD_QUOTIENT"));
                    x.ProposerRewardQuotient = section.GetValue(nameof(x.ProposerRewardQuotient),
                        () => configuration.GetValue<ulong>("PROPOSER_REWARD_QUOTIENT"));
                    x.InactivityPenaltyQuotient = section.GetValue(nameof(x.InactivityPenaltyQuotient),
                        () => configuration.GetValue<ulong>("INACTIVITY_PENALTY_QUOTIENT"));
                    x.MinimumSlashingPenaltyQuotient = section.GetValue(nameof(x.MinimumSlashingPenaltyQuotient),
                        () => configuration.GetValue<ulong>("MIN_SLASHING_PENALTY_QUOTIENT"));
                });
            });
            services.Configure<MaxOperationsPerBlock>(x =>
            {
                configuration.Bind("BeaconChain:MaxOperationsPerBlock", section =>
                {
                    x.MaximumProposerSlashings = section.GetValue(nameof(x.MaximumProposerSlashings),
                        () => configuration.GetValue<ulong>("MAX_PROPOSER_SLASHINGS"));
                    x.MaximumAttesterSlashings = section.GetValue(nameof(x.MaximumAttesterSlashings),
                        () => configuration.GetValue<ulong>("MAX_ATTESTER_SLASHINGS"));
                    x.MaximumAttestations = section.GetValue(nameof(x.MaximumAttestations),
                        () => configuration.GetValue<ulong>("MAX_ATTESTATIONS"));
                    x.MaximumDeposits = section.GetValue(nameof(x.MaximumDeposits),
                        () => configuration.GetValue<ulong>("MAX_DEPOSITS"));
                    x.MaximumVoluntaryExits = section.GetValue(nameof(x.MaximumVoluntaryExits),
                        () => configuration.GetValue<ulong>("MAX_VOLUNTARY_EXITS"));
                });
            });
            services.Configure<SignatureDomains>(x =>
            {
                configuration.Bind("BeaconChain:SignatureDomains", section =>
                {
                    x.BeaconProposer =  (DomainType)BinaryPrimitives.ReadUInt32LittleEndian(
                        section.GetBytesFromPrefixedHex("DomainBeaconProposer",
                            () => configuration.GetBytesFromPrefixedHex("DOMAIN_BEACON_PROPOSER",
                                () => new byte[4])));
                    x.BeaconAttester = (DomainType)BinaryPrimitives.ReadUInt32LittleEndian(
                        section.GetBytesFromPrefixedHex("DomainBeaconAttester",
                            () => configuration.GetBytesFromPrefixedHex("DOMAIN_BEACON_ATTESTER",
                                () => new byte[4])));
                    x.Randao = (DomainType)BinaryPrimitives.ReadUInt32LittleEndian(
                        section.GetBytesFromPrefixedHex("DomainRandao",
                            () => configuration.GetBytesFromPrefixedHex("DOMAIN_RANDAO",
                                () => new byte[4])));
                    x.Deposit = (DomainType)BinaryPrimitives.ReadUInt32LittleEndian(
                        section.GetBytesFromPrefixedHex("DomainDeposit",
                            () => configuration.GetBytesFromPrefixedHex("DOMAIN_DEPOSIT",
                                () => new byte[4])));
                    x.VoluntaryExit = (DomainType)BinaryPrimitives.ReadUInt32LittleEndian(
                        section.GetBytesFromPrefixedHex("DomainVoluntaryExit",
                            () => configuration.GetBytesFromPrefixedHex("DOMAIN_VOLUNTARY_EXIT",
                                () => new byte[4])));
                });
            });
            services.Configure<ForkChoiceConfiguration>(x =>
            {
                x.SafeSlotsToUpdateJustified = new Slot(
                    configuration.GetValue<ulong>("ForkChoiceConfiguration:SafeSlotsToUpdateJustified",
                        () => configuration.GetValue<ulong>("SAFE_SLOTS_TO_UPDATE_JUSTIFIED")));
            });
        }
    }
}
