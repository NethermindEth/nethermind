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

using System;
using System.IO.Abstractions;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nethermind.BeaconNode.Storage;
using Nethermind.Core2;
using Nethermind.Core2.Configuration;
using Nethermind.Core2.Types;
using Nethermind.Merkleization;
using NSubstitute;
using Shouldly;

namespace Nethermind.BeaconNode.Test.Genesis
{
    [TestClass]
    public class ChainStartTest
    {
        [TestMethod]
        public async Task GenesisWithEmptyParametersTimeShouldReject()
        {
            // Arrange
            IServiceProvider testServiceProvider = TestSystem.BuildTestServiceProvider();

            ChainConstants chainConstants = testServiceProvider.GetService<ChainConstants>();
            IOptionsMonitor<MiscellaneousParameters> miscellaneousParameterOptions =
                testServiceProvider.GetService<IOptionsMonitor<MiscellaneousParameters>>();
            IOptionsMonitor<GweiValues> gweiValueOptions =
                testServiceProvider.GetService<IOptionsMonitor<GweiValues>>();
            IOptionsMonitor<InitialValues> initialValueOptions =
                testServiceProvider.GetService<IOptionsMonitor<InitialValues>>();
            IOptionsMonitor<TimeParameters> timeParameterOptions =
                testServiceProvider.GetService<IOptionsMonitor<TimeParameters>>();
            IOptionsMonitor<StateListLengths> stateListLengthOptions =
                testServiceProvider.GetService<IOptionsMonitor<StateListLengths>>();
            IOptionsMonitor<RewardsAndPenalties> rewardsAndPenaltiesOptions =
                testServiceProvider.GetService<IOptionsMonitor<RewardsAndPenalties>>();
            IOptionsMonitor<MaxOperationsPerBlock> maxOperationsPerBlockOptions =
                testServiceProvider.GetService<IOptionsMonitor<MaxOperationsPerBlock>>();
            IOptionsMonitor<ForkChoiceConfiguration> forkChoiceConfigurationOptions =
                testServiceProvider.GetService<IOptionsMonitor<ForkChoiceConfiguration>>();
            IOptionsMonitor<SignatureDomains> signatureDomainOptions =
                testServiceProvider.GetService<IOptionsMonitor<SignatureDomains>>();
            IOptionsMonitor<InMemoryConfiguration> inMemoryConfigurationOptions =
                testServiceProvider.GetService<IOptionsMonitor<InMemoryConfiguration>>();

            miscellaneousParameterOptions.CurrentValue.MinimumGenesisActiveValidatorCount = 2;

            LoggerFactory loggerFactory = new LoggerFactory(new[]
            {
                new ConsoleLoggerProvider(Core2.Configuration.Static.OptionsMonitor<ConsoleLoggerOptions>())
            });

            ICryptographyService cryptographyService = testServiceProvider.GetService<ICryptographyService>();
            IDepositStore depositStore = new DepositStore(cryptographyService, chainConstants);
            
            BeaconChainUtility beaconChainUtility = new BeaconChainUtility(
                loggerFactory.CreateLogger<BeaconChainUtility>(),
                chainConstants, miscellaneousParameterOptions, initialValueOptions, gweiValueOptions,
                timeParameterOptions,
                cryptographyService);
            BeaconStateAccessor beaconStateAccessor = new BeaconStateAccessor(chainConstants,
                miscellaneousParameterOptions, timeParameterOptions, stateListLengthOptions, signatureDomainOptions,
                cryptographyService, beaconChainUtility);
            BeaconStateMutator beaconStateMutator = new BeaconStateMutator(chainConstants, timeParameterOptions,
                stateListLengthOptions, rewardsAndPenaltiesOptions,
                beaconChainUtility, beaconStateAccessor);
            BeaconStateTransition beaconStateTransition = new BeaconStateTransition(
                loggerFactory.CreateLogger<BeaconStateTransition>(),
                chainConstants, gweiValueOptions, timeParameterOptions, stateListLengthOptions,
                rewardsAndPenaltiesOptions, maxOperationsPerBlockOptions, signatureDomainOptions,
                cryptographyService, beaconChainUtility, beaconStateAccessor, beaconStateMutator, depositStore);
            SimpleLatestMessageDrivenGreedyHeaviestObservedSubtree simpleLmdGhost =
                new SimpleLatestMessageDrivenGreedyHeaviestObservedSubtree(
                    loggerFactory.CreateLogger<SimpleLatestMessageDrivenGreedyHeaviestObservedSubtree>(),
                    chainConstants, beaconChainUtility, beaconStateAccessor);
            MemoryStore store = new MemoryStore(loggerFactory.CreateLogger<MemoryStore>(), inMemoryConfigurationOptions,
                new DataDirectory("data"), Substitute.For<IFileSystem>(), simpleLmdGhost, new StoreAccessor());
            ForkChoice forkChoice = new ForkChoice(loggerFactory.CreateLogger<ForkChoice>(),
                chainConstants, miscellaneousParameterOptions, timeParameterOptions, maxOperationsPerBlockOptions,
                forkChoiceConfigurationOptions, signatureDomainOptions,
                cryptographyService, beaconChainUtility, beaconStateAccessor, beaconStateTransition);

            GenesisChainStart genesisChainStart = new GenesisChainStart(loggerFactory.CreateLogger<GenesisChainStart>(),
                chainConstants, miscellaneousParameterOptions, gweiValueOptions, initialValueOptions,
                timeParameterOptions, stateListLengthOptions,
                cryptographyService, store, beaconStateAccessor, beaconStateTransition, forkChoice, depositStore);

            // Act
            Bytes32 eth1BlockHash = Bytes32.Zero;
            ulong eth1Timestamp = 106185600uL; // 1973-05-14
            bool success = await genesisChainStart.TryGenesisAsync(eth1BlockHash, eth1Timestamp);

            // Assert
            success.ShouldBeFalse();
        }
    }
}