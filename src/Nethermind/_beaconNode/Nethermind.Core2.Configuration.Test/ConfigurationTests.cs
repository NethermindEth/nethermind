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

using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nethermind.Core2.Configuration;
using Nethermind.Core2.Types;
using Shouldly;

namespace Nethermind.Core2.Configuration.Test
{
    [TestClass]
    public class ConfigurationTests
    {
        [TestMethod]
        public void JsonDevelopmentConfig()
        {
            // Arrange
            var services = new ServiceCollection();
            services.AddLogging(configure => configure.AddConsole());
            var configuration = new ConfigurationBuilder()
                .AddJsonFile("Development/appsettings.json")
                .Build();
            services.ConfigureBeaconChain(configuration);
            var testServiceProvider = services.BuildServiceProvider();

            // Act
            var miscellaneousParameters = testServiceProvider.GetService<IOptions<MiscellaneousParameters>>().Value;
            ForkChoiceConfiguration forkChoiceConfiguration = testServiceProvider.GetService<IOptions<ForkChoiceConfiguration>>().Value;
            HonestValidatorConstants honestValidatorConstants = testServiceProvider.GetService<IOptions<HonestValidatorConstants>>().Value;
            var gweiValues = testServiceProvider.GetService<IOptions<GweiValues>>().Value;
            var initialValues = testServiceProvider.GetService<IOptions<InitialValues>>().Value;
            var timeParameters = testServiceProvider.GetService<IOptions<TimeParameters>>().Value;
            var stateListLengths = testServiceProvider.GetService<IOptions<StateListLengths>>().Value;
            var rewardsAndPenalties = testServiceProvider.GetService<IOptions<RewardsAndPenalties>>().Value;
            var maxOperationsPerBlock = testServiceProvider.GetService<IOptions<MaxOperationsPerBlock>>().Value;
            var signatureDomains = testServiceProvider.GetService<IOptions<SignatureDomains>>().Value;

            // Assert
            ValidateConfigShouldHaveValues(miscellaneousParameters, forkChoiceConfiguration, honestValidatorConstants, gweiValues, initialValues, timeParameters, stateListLengths, rewardsAndPenalties, maxOperationsPerBlock, signatureDomains);
        }

        [TestMethod]
        public void YamlMinimalConfig()
        {
            // Arrange
            var services = new ServiceCollection();
            services.AddLogging(configure => configure.AddConsole());
            var configuration = new ConfigurationBuilder()
                .AddYamlFile("minimal.yaml")
                .Build();
            services.ConfigureBeaconChain(configuration);
            var testServiceProvider = services.BuildServiceProvider();

            // Act
            var miscellaneousParameters = testServiceProvider.GetService<IOptions<MiscellaneousParameters>>().Value;
            ForkChoiceConfiguration forkChoiceConfiguration = testServiceProvider.GetService<IOptions<ForkChoiceConfiguration>>().Value;
            HonestValidatorConstants honestValidatorConstants = testServiceProvider.GetService<IOptions<HonestValidatorConstants>>().Value;
            var gweiValues = testServiceProvider.GetService<IOptions<GweiValues>>().Value;
            var initialValues = testServiceProvider.GetService<IOptions<InitialValues>>().Value;
            var timeParameters = testServiceProvider.GetService<IOptions<TimeParameters>>().Value;
            var stateListLengths = testServiceProvider.GetService<IOptions<StateListLengths>>().Value;
            var rewardsAndPenalties = testServiceProvider.GetService<IOptions<RewardsAndPenalties>>().Value;
            var maxOperationsPerBlock = testServiceProvider.GetService<IOptions<MaxOperationsPerBlock>>().Value;
            var signatureDomains = testServiceProvider.GetService<IOptions<SignatureDomains>>().Value;

            // Assert
            ValidateConfigShouldHaveValues(miscellaneousParameters, forkChoiceConfiguration, honestValidatorConstants, gweiValues, initialValues, timeParameters, stateListLengths, rewardsAndPenalties, maxOperationsPerBlock, signatureDomains);
        }

        [TestMethod]
        public void BothWithOverride() 
        {
            // Arrange
            var services = new ServiceCollection();
            services.AddLogging(configure => configure.AddConsole());
            var configuration = new ConfigurationBuilder()
                .AddYamlFile("testconfigA.yaml")
                .AddJsonFile("testappsettingsB.json")
                .Build();
            services.ConfigureBeaconChain(configuration);
            var testServiceProvider = services.BuildServiceProvider();

            // Act
            var miscellaneousParameters = testServiceProvider.GetService<IOptions<MiscellaneousParameters>>().Value;
            var gweiValues = testServiceProvider.GetService<IOptions<GweiValues>>().Value;
            var timeParameters = testServiceProvider.GetService<IOptions<TimeParameters>>().Value;

            // Assert

            // yaml only
            miscellaneousParameters.MaximumCommitteesPerSlot.ShouldBe(11uL);
            // json only
            miscellaneousParameters.TargetCommitteeSize.ShouldBe(22uL);
            // both yaml and json (jsonshould override)
            miscellaneousParameters.MaximumValidatorsPerCommittee.ShouldBe(23uL);
            // json only, different section
            gweiValues.MaximumEffectiveBalance.ShouldBe(new Gwei(24uL));
            // yaml only, no section in json
            timeParameters.SecondsPerSlot.ShouldBe(15U);
        }
        
        private static void ValidateConfigShouldHaveValues(MiscellaneousParameters miscellaneousParameters,
            ForkChoiceConfiguration forkChoiceConfiguration, HonestValidatorConstants honestValidatorConstants,
            GweiValues gweiValues, InitialValues initialValues, TimeParameters timeParameters,
            StateListLengths stateListLengths, RewardsAndPenalties rewardsAndPenalties,
            MaxOperationsPerBlock maxOperationsPerBlock, SignatureDomains signatureDomains)
        {
            miscellaneousParameters.ChurnLimitQuotient.ShouldNotBe(0uL);
            miscellaneousParameters.MaximumCommitteesPerSlot.ShouldNotBe(0uL);
            miscellaneousParameters.MaximumValidatorsPerCommittee.ShouldNotBe(0uL);
            miscellaneousParameters.MinimumGenesisActiveValidatorCount.ShouldNotBe(0);
            miscellaneousParameters.MinimumGenesisTime.ShouldNotBe(0uL);
            miscellaneousParameters.MinimumPerEpochChurnLimit.ShouldNotBe(0uL);
            miscellaneousParameters.ShuffleRoundCount.ShouldNotBe(0);
            miscellaneousParameters.TargetCommitteeSize.ShouldNotBe(0uL);
            
            forkChoiceConfiguration.SafeSlotsToUpdateJustified.ShouldNotBe(Slot.Zero);
            
            honestValidatorConstants.EpochsPerRandomSubnetSubscription.ShouldNotBe(Epoch.Zero);
            honestValidatorConstants.Eth1FollowDistance.ShouldNotBe(0uL);
            honestValidatorConstants.RandomSubnetsPerValidator.ShouldNotBe(0uL);
            honestValidatorConstants.SecondsPerEth1Block.ShouldNotBe(0uL);
            honestValidatorConstants.TargetAggregatorsPerCommittee.ShouldNotBe(0uL);
            

            gweiValues.EffectiveBalanceIncrement.ShouldNotBe(Gwei.Zero);
            gweiValues.EjectionBalance.ShouldNotBe(Gwei.Zero);
            gweiValues.MaximumEffectiveBalance.ShouldNotBe(Gwei.Zero);

            // actually should be zero
            initialValues.BlsWithdrawalPrefix.ShouldBe((byte) 0);

            initialValues.GenesisForkVersion.ShouldBe(new ForkVersion(new byte[] {0x00, 0x00, 0x00, 0x01}));

            timeParameters.MaximumSeedLookahead.ShouldNotBe(Epoch.Zero);
            timeParameters.MinimumAttestationInclusionDelay.ShouldNotBe(Slot.Zero);
            timeParameters.MinimumGenesisDelay.ShouldNotBe(0u);
            timeParameters.MinimumEpochsToInactivityPenalty.ShouldNotBe(Epoch.Zero);
            timeParameters.MinimumSeedLookahead.ShouldNotBe(Epoch.Zero);
            timeParameters.MinimumValidatorWithdrawabilityDelay.ShouldNotBe(Epoch.Zero);
            timeParameters.PersistentCommitteePeriod.ShouldNotBe(Epoch.Zero);
            timeParameters.SecondsPerSlot.ShouldNotBe(0U);
            timeParameters.SlotsPerEpoch.ShouldNotBe(0U);
            timeParameters.SlotsPerEth1VotingPeriod.ShouldNotBe(0U);
            timeParameters.SlotsPerHistoricalRoot.ShouldNotBe(0U);

            stateListLengths.EpochsPerHistoricalVector.ShouldNotBe(0U);
            stateListLengths.EpochsPerSlashingsVector.ShouldNotBe(0U);
            stateListLengths.HistoricalRootsLimit.ShouldNotBe(0uL);
            stateListLengths.ValidatorRegistryLimit.ShouldNotBe(0uL);

            rewardsAndPenalties.BaseRewardFactor.ShouldNotBe(0uL);
            rewardsAndPenalties.InactivityPenaltyQuotient.ShouldNotBe(0uL);
            rewardsAndPenalties.MinimumSlashingPenaltyQuotient.ShouldNotBe(0uL);
            rewardsAndPenalties.ProposerRewardQuotient.ShouldNotBe(0uL);
            rewardsAndPenalties.WhistleblowerRewardQuotient.ShouldNotBe(0uL);

            maxOperationsPerBlock.MaximumAttestations.ShouldNotBe(0uL);
            maxOperationsPerBlock.MaximumAttesterSlashings.ShouldNotBe(0uL);
            maxOperationsPerBlock.MaximumDeposits.ShouldNotBe(0uL);
            maxOperationsPerBlock.MaximumProposerSlashings.ShouldNotBe(0uL);
            maxOperationsPerBlock.MaximumVoluntaryExits.ShouldNotBe(0uL);

            // actually should be zero
            signatureDomains.BeaconProposer.ShouldBe(default);

            signatureDomains.BeaconAttester.ShouldNotBe(default);
            signatureDomains.Deposit.ShouldNotBe(default);
            signatureDomains.Randao.ShouldNotBe(default);
            signatureDomains.VoluntaryExit.ShouldNotBe(default);
        }
    }
}
