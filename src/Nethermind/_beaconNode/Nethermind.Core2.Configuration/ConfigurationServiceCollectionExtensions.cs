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

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nethermind.Core2.Types;

namespace Nethermind.Core2.Configuration
{
    public static class ConfigurationServiceCollectionExtensions
    {
        public static void ConfigureBeaconChain(this IServiceCollection services, IConfiguration configuration)
        {
            services.AddSingleton<IClientVersion, ClientVersion>();

            services.AddSingleton<ChainConstants>();

            services.AddSingleton(new DataDirectory(configuration.GetValue<string>(DataDirectory.Key)));

            services.Configure<AnchorState>(x => configuration.Bind("AnchorState", x));

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
                    x.MinimumGenesisActiveValidatorCount = section.GetValue(
                        nameof(x.MinimumGenesisActiveValidatorCount),
                        () => configuration.GetValue<int>("MIN_GENESIS_ACTIVE_VALIDATOR_COUNT"));
                    x.MinimumGenesisTime = section.GetValue(nameof(x.MinimumGenesisTime),
                        () => configuration.GetValue<ulong>("MIN_GENESIS_TIME"));
                });
            });
            services.Configure<ForkChoiceConfiguration>(x =>
            {
                x.SafeSlotsToUpdateJustified = new Slot(
                    configuration.GetValue<ulong>("BeaconChain:ForkChoice:SafeSlotsToUpdateJustified",
                        () => configuration.GetValue<ulong>("SAFE_SLOTS_TO_UPDATE_JUSTIFIED")));
            });
            services.Configure<HonestValidatorConstants>(x =>
            {
                configuration.Bind("BeaconChain:Validator", section =>
                {
                    x.Eth1FollowDistance = section.GetValue(nameof(x.Eth1FollowDistance),
                        () => configuration.GetValue<ulong>("ETH1_FOLLOW_DISTANCE"));
                    x.TargetAggregatorsPerCommittee = section.GetValue(nameof(x.TargetAggregatorsPerCommittee),
                        () => configuration.GetValue<ulong>("TARGET_AGGREGATORS_PER_COMMITTEE"));
                    x.RandomSubnetsPerValidator = section.GetValue(nameof(x.RandomSubnetsPerValidator),
                        () => configuration.GetValue<ulong>("RANDOM_SUBNETS_PER_VALIDATOR"));
                    x.EpochsPerRandomSubnetSubscription = new Epoch(
                        section.GetValue(nameof(x.Eth1FollowDistance),
                            () => configuration.GetValue<ulong>("EPOCHS_PER_RANDOM_SUBNET_SUBSCRIPTION")));
                    x.SecondsPerEth1Block = section.GetValue(nameof(x.SecondsPerEth1Block),
                        () => configuration.GetValue<ulong>("SECONDS_PER_ETH1_BLOCK"));
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
                    x.BlsWithdrawalPrefix = section.GetValue<byte>("BlsWithdrawalPrefix",
                        () => configuration.GetValue<byte>("BLS_WITHDRAWAL_PREFIX"));
                    x.GenesisForkVersion = new ForkVersion(
                        section.GetBytesFromPrefixedHex("GenesisForkVersion",
                            () => configuration.GetBytesFromPrefixedHex("GENESIS_FORK_VERSION",
                                () => new byte[ForkVersion.Length])));
                });
            });
            services.Configure<TimeParameters>(x =>
            {
                configuration.Bind("BeaconChain:TimeParameters", section =>
                {
                    x.MinimumGenesisDelay = section.GetValue("MinimumGenesisDelay",
                        () => configuration.GetValue<uint>("MIN_GENESIS_DELAY"));
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
                    x.BeaconProposer = new DomainType(
                        section.GetBytesFromPrefixedHex("DomainBeaconProposer",
                            () => configuration.GetBytesFromPrefixedHex("DOMAIN_BEACON_PROPOSER",
                                () => new byte[DomainType.Length])));
                    x.BeaconAttester = new DomainType(
                        section.GetBytesFromPrefixedHex("DomainBeaconAttester",
                            () => configuration.GetBytesFromPrefixedHex("DOMAIN_BEACON_ATTESTER",
                                () => new byte[DomainType.Length])));
                    x.Randao = new DomainType(
                        section.GetBytesFromPrefixedHex("DomainRandao",
                            () => configuration.GetBytesFromPrefixedHex("DOMAIN_RANDAO",
                                () => new byte[DomainType.Length])));
                    x.Deposit = new DomainType(
                        section.GetBytesFromPrefixedHex("DomainDeposit",
                            () => configuration.GetBytesFromPrefixedHex("DOMAIN_DEPOSIT",
                                () => new byte[DomainType.Length])));
                    x.VoluntaryExit = new DomainType(
                        section.GetBytesFromPrefixedHex("DomainVoluntaryExit",
                            () => configuration.GetBytesFromPrefixedHex("DOMAIN_VOLUNTARY_EXIT",
                                () => new byte[DomainType.Length])));
                });
            });
        }
    }
}