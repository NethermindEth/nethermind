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
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nethermind.BeaconNode.Test.Helpers;
using Nethermind.Core2;
using Nethermind.Core2.Configuration;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.P2p;
using Nethermind.Core2.Types;
using NSubstitute;
using NSubstitute.Core;
using Shouldly;

namespace Nethermind.BeaconNode.Test
{
    [TestClass]
    public class SynchronisationManagerTests
    {
        [TestMethod]
        public async Task ShouldRequestBlocksFromAheadFinalizedCheckpoint()
        {
            // Arrange
            IServiceCollection testServiceCollection = TestSystem.BuildTestServiceCollection(useStore: true);
            INetworkPeering mockNetworkPeering = Substitute.For<INetworkPeering>();
            testServiceCollection.AddSingleton<INetworkPeering>(mockNetworkPeering);
            ServiceProvider testServiceProvider = testServiceCollection.BuildServiceProvider();
            
            BeaconState state = TestState.PrepareTestState(testServiceProvider);
            IForkChoice forkChoice = testServiceProvider.GetService<IForkChoice>();
            // Get genesis store initialise MemoryStoreProvider with the state
            IStore store = testServiceProvider.GetService<IStore>();
            await forkChoice.InitializeForkChoiceStoreAsync(store, state);            
            
            // Act
            PeeringStatus peeringStatus = new PeeringStatus(
                new ForkVersion(new byte[4] {0, 0, 0, 0}),
                new Root(Enumerable.Repeat((byte) 0x34, 32).ToArray()),
                Epoch.One,
                new Root(Enumerable.Repeat((byte) 0x56, 32).ToArray()),
                new Slot(2));
            ISynchronizationManager synchronizationManager = testServiceProvider.GetService<ISynchronizationManager>();
            await synchronizationManager.OnStatusResponseReceived("peer", peeringStatus);
            
            // Assert
            await mockNetworkPeering.Received(1)
                .RequestBlocksAsync("peer", Arg.Any<Root>(), Arg.Any<Slot>(), Arg.Any<Slot>());
            await mockNetworkPeering.DidNotReceive().DisconnectPeerAsync("peer");
        }

        [TestMethod]
        public async Task ShouldNotRequestIfHeadBehind()
        {
            // Arrange
            IServiceCollection testServiceCollection = TestSystem.BuildTestServiceCollection(useStore: true);
            INetworkPeering mockNetworkPeering = Substitute.For<INetworkPeering>();
            testServiceCollection.AddSingleton<INetworkPeering>(mockNetworkPeering);
            ServiceProvider testServiceProvider = testServiceCollection.BuildServiceProvider();
            
            BeaconState state = TestState.PrepareTestState(testServiceProvider);
            IForkChoice forkChoice = testServiceProvider.GetService<IForkChoice>();
            // Get genesis store initialise MemoryStoreProvider with the state
            IStore store = testServiceProvider.GetService<IStore>();
            await forkChoice.InitializeForkChoiceStoreAsync(store, state);            
            
            // Move forward time
            TimeParameters timeParameters = testServiceProvider.GetService<IOptions<TimeParameters>>().Value;
            ulong targetTime = 2 * 6; // slot 2
            for (ulong timeSinceGenesis = 1; timeSinceGenesis <= targetTime; timeSinceGenesis++)
            {
                ulong time = state.GenesisTime + timeSinceGenesis;
                await forkChoice.OnTickAsync(store, time);
                if (timeSinceGenesis % timeParameters.SecondsPerSlot == 0)
                {
                    BeaconBlock block = TestBlock.BuildEmptyBlockForNextSlot(testServiceProvider, state, BlsSignature.Zero);
                    SignedBeaconBlock signedBlock = TestState.StateTransitionAndSignBlock(testServiceProvider, state, block);
                    await forkChoice.OnBlockAsync(store, signedBlock);
                }
            }
            
            // Act
            PeeringStatus peeringStatus = new PeeringStatus(
                new ForkVersion(new byte[4] {0, 0, 0, 0}),
                Root.Zero,
                Epoch.Zero,
                new Root(Enumerable.Repeat((byte) 0x56, 32).ToArray()),
                new Slot(1));
            ISynchronizationManager synchronizationManager = testServiceProvider.GetService<ISynchronizationManager>();
            await synchronizationManager.OnStatusResponseReceived("peer", peeringStatus);
            
            // Assert
            await mockNetworkPeering.DidNotReceive()
                .RequestBlocksAsync("peer", Arg.Any<Root>(), Arg.Any<Slot>(), Arg.Any<Slot>());
            await mockNetworkPeering.DidNotReceive().DisconnectPeerAsync("peer");
        }
        
        [TestMethod]
        public async Task ShouldDisconnectStatusWrongFork()
        {
            // Arrange
            IServiceCollection testServiceCollection = TestSystem.BuildTestServiceCollection(useStore: true);
            INetworkPeering mockNetworkPeering = Substitute.For<INetworkPeering>();
            testServiceCollection.AddSingleton<INetworkPeering>(mockNetworkPeering);
            ServiceProvider testServiceProvider = testServiceCollection.BuildServiceProvider();
            
            BeaconState state = TestState.PrepareTestState(testServiceProvider);
            IForkChoice forkChoice = testServiceProvider.GetService<IForkChoice>();
            // Get genesis store initialise MemoryStoreProvider with the state
            IStore store = testServiceProvider.GetService<IStore>();
            await forkChoice.InitializeForkChoiceStoreAsync(store, state);            
            
            // Act
            PeeringStatus peeringStatus = new PeeringStatus(
                new ForkVersion(new byte[4] {1, 2, 3, 4}),
                Root.Zero,
                Epoch.Zero,
                new Root(Enumerable.Repeat((byte) 0x56, 32).ToArray()),
                Slot.Zero);
            ISynchronizationManager synchronizationManager = testServiceProvider.GetService<ISynchronizationManager>();
            await synchronizationManager.OnStatusResponseReceived("peer", peeringStatus);
            
            // Assert
            await mockNetworkPeering.Received(1).DisconnectPeerAsync("peer");
            await mockNetworkPeering.DidNotReceive()
                .RequestBlocksAsync("peer", Arg.Any<Root>(), Arg.Any<Slot>(), Arg.Any<Slot>());
        }

    }
}