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

using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nethermind.BeaconNode;
using Nethermind.BeaconNode.Services;
using System.Net.Http;
using Nethermind.BeaconNode.Configuration;
using Nethermind.Core2.Types;
using Nethermind.HonestValidator.Configuration;
using Nethermind.HonestValidator.Services;

namespace Nethermind.HonestValidator
{
    public static class HonestValidatorServiceCollectionExtensions
    {
        public static void AddHonestValidator(this IServiceCollection services, IConfiguration configuration)
        {
            AddConfiguration(services, configuration);
            
            services.AddSingleton<IClock, SystemClock>();
            services.AddSingleton<ICryptographyService, CortexCryptographyService>();
            services.AddSingleton<BeaconChain>();
            services.AddSingleton<ValidatorClient>();
            services.AddSingleton<ClientVersion>();
            
            services.AddSingleton<IBeaconNodeApi, BeaconNodeProxy>();

            services.AddHostedService<HonestValidatorWorker>();
        }
        
        private static void AddConfiguration(IServiceCollection services, IConfiguration configuration)
        {
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
            services.Configure<BeaconNodeConnection>(x =>
            {
                configuration.Bind("BeaconNodeConnection", section =>
                {
                    x.RemoteUrls = section.GetSection(nameof(x.RemoteUrls)).Get<string[]>();
                    x.ConnectionFailureLoopMillisecondsDelay = section.GetValue<int>("ConnectionFailureLoopMillisecondsDelay", 1000);
                });
            });
        }
    }
}
