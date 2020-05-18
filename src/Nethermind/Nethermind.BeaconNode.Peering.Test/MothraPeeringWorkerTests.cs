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
using System.Diagnostics.CodeAnalysis;
using System.IO.Abstractions;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using Nethermind.Core2;
using Nethermind.Core2.Configuration;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.P2p;
using Nethermind.Core2.Types;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Nethermind.BeaconNode.Peering.Test
{
    [TestFixture]
    public class MothraPeeringWorkerTests
    {
        private IOptionsMonitor<ConsoleLoggerOptions>? _mockLoggerOptionsMonitor;
        private LoggerFactory? _loggerFactory;
        private MockMothra? _mockMothra;
        private IForkChoice? _mockForkChoice;
        private ISynchronizationManager? _mockSynchronizationManager;
        private IStore? _mockStore;
        private IOptionsMonitor<MothraConfiguration>? _mockMothraConfigurationMonitor;
        private INetworkPeering? _mockNetworkPeering;
        private PeerManager? _peerManager;
        private PeerDiscoveredProcessor? _peerDiscoveredProcessor;
        private RpcPeeringStatusProcessor? _rpcPeeringStatusProcessor;
        private RpcBeaconBlocksByRangeProcessor? _rpcBeaconBlocksByRangeProcessor;
        private SignedBeaconBlockProcessor? _signedBeaconBlockProcessor;
        private DataDirectory? _dataDirectory;

        [SetUp]
        public void SetUp()
        {
            _mockLoggerOptionsMonitor = Substitute.For<IOptionsMonitor<ConsoleLoggerOptions>>();
            _mockLoggerOptionsMonitor.CurrentValue.Returns(new ConsoleLoggerOptions()
            {
                Format = ConsoleLoggerFormat.Systemd,
                DisableColors = true,
                IncludeScopes = true,
                TimestampFormat = " HH':'mm':'sszz "
            });
            _loggerFactory = new LoggerFactory(new [] { new ConsoleLoggerProvider(_mockLoggerOptionsMonitor) });
            
            _mockMothra = new MockMothra();
            // mockMothra.StartCalled += settings =>
            // {
            //     ThreadPool.QueueUserWorkItem(x =>
            //     {
            //         Thread.Sleep(TimeSpan.FromMilliseconds(100));
            //         byte[] peerUtf8 = Encoding.UTF8.GetBytes("peer1");
            //         mockMothra.RaisePeerDiscovered(peerUtf8);
            //     });
            // };
            
            _mockForkChoice = Substitute.For<IForkChoice>();
            _mockSynchronizationManager = Substitute.For<ISynchronizationManager>();
            _mockStore = Substitute.For<IStore>();
            _mockStore.IsInitialized.Returns(true);
            _mockMothraConfigurationMonitor = Substitute.For<IOptionsMonitor<MothraConfiguration>>();
            _mockMothraConfigurationMonitor.CurrentValue.Returns(new MothraConfiguration());
            
            // TODO: Replace with MothraNetworkPeering and mockMothra.
            _mockNetworkPeering = Substitute.For<INetworkPeering>();
            
            _dataDirectory = new DataDirectory("data");

            _peerManager = new PeerManager(_loggerFactory.CreateLogger<PeerManager>());
            _peerDiscoveredProcessor = new PeerDiscoveredProcessor(
                _loggerFactory.CreateLogger<PeerDiscoveredProcessor>(), _mockSynchronizationManager, _peerManager);
            _rpcPeeringStatusProcessor = new RpcPeeringStatusProcessor(
                _loggerFactory.CreateLogger<RpcPeeringStatusProcessor>(), _mockSynchronizationManager, _peerManager);
            _rpcBeaconBlocksByRangeProcessor = new RpcBeaconBlocksByRangeProcessor(_loggerFactory.CreateLogger<RpcBeaconBlocksByRangeProcessor>(),
                _mockNetworkPeering, _mockForkChoice, _mockStore);
            _signedBeaconBlockProcessor = new SignedBeaconBlockProcessor(
                _loggerFactory.CreateLogger<SignedBeaconBlockProcessor>(), _mockMothraConfigurationMonitor,
                Substitute.For<IFileSystem>(), _mockForkChoice, _mockStore, _dataDirectory, _peerManager);
        }
        
        [Test]
        public async Task StartWorkerShouldStartMothra()
        {
            // arrange
            MothraPeeringWorker peeringWorker = new MothraPeeringWorker(
                _loggerFactory.CreateLogger<MothraPeeringWorker>(),
                _mockMothraConfigurationMonitor!,
                Substitute.For<IHostEnvironment>(),
                Substitute.For<IClientVersion>(),
                _mockStore!,
                _mockMothra!,
                _dataDirectory!,
                _peerManager!,
                _peerDiscoveredProcessor!,
                _rpcPeeringStatusProcessor!,
                _rpcBeaconBlocksByRangeProcessor!,
                _signedBeaconBlockProcessor!
            );
        
            // act
            await peeringWorker.StartAsync(CancellationToken.None);
            await Task.Delay(TimeSpan.FromMilliseconds(100));
            await peeringWorker.StopAsync(CancellationToken.None);
            
            // assert
            _mockMothra!.StartCalls.Count.ShouldBe(1);
            // mockMothra.SendRpcResponseCalls.Count.ShouldBe(1);
            // Encoding.UTF8.GetString(mockMothra.SendRpcResponseCalls[0].peerUtf8).ShouldBe("peer1");
            // Encoding.UTF8.GetString(mockMothra.SendRpcResponseCalls[0].methodUtf8).ShouldBe("/eth2/beacon_chain/req/status/1/");
        }
        
        [Test]
        public async Task PeerDiscoveredShouldCreatePeerAndInSession()
        {
            // arrange
            MothraPeeringWorker peeringWorker = new MothraPeeringWorker(
                _loggerFactory.CreateLogger<MothraPeeringWorker>(),
                _mockMothraConfigurationMonitor!,
                Substitute.For<IHostEnvironment>(),
                Substitute.For<IClientVersion>(),
                _mockStore!,
                _mockMothra!,
                _dataDirectory!,
                _peerManager!,
                _peerDiscoveredProcessor!,
                _rpcPeeringStatusProcessor!,
                _rpcBeaconBlocksByRangeProcessor!,
                _signedBeaconBlockProcessor!
            );
        
            // act - start worker
            await peeringWorker.StartAsync(CancellationToken.None);
            // - wait for startup
            await Task.Delay(TimeSpan.FromMilliseconds(100));
            // - raise event
            _mockMothra!.RaisePeerDiscovered(Encoding.UTF8.GetBytes("peer1"));
            // - wait for event to be handled
            await Task.Delay(TimeSpan.FromMilliseconds(100));
            // - finish
            await peeringWorker.StopAsync(CancellationToken.None);

            // assert
            _peerManager!.Peers.Count.ShouldBe(1);
            
            Session session = _peerManager.Sessions["peer1"].First();
            session.Direction.ShouldBe(ConnectionDirection.In);
            session.State.ShouldBe(SessionState.New);
            session.Peer.Id.ShouldBe("peer1");
            session.Peer.Status.ShouldBeNull();
        }

        [Test]
        public async Task PeerDiscoveredWhenExpectedShouldCreatePeerAndOutSession()
        {
            // arrange
            _peerManager!.AddExpectedPeer("enr:123");
            
            MothraPeeringWorker peeringWorker = new MothraPeeringWorker(
                _loggerFactory.CreateLogger<MothraPeeringWorker>(),
                _mockMothraConfigurationMonitor!,
                Substitute.For<IHostEnvironment>(),
                Substitute.For<IClientVersion>(),
                _mockStore!,
                _mockMothra!,
                _dataDirectory!,
                _peerManager,
                _peerDiscoveredProcessor!,
                _rpcPeeringStatusProcessor!,
                _rpcBeaconBlocksByRangeProcessor!,
                _signedBeaconBlockProcessor!
            );
            
            // act - start worker
            await peeringWorker.StartAsync(CancellationToken.None);
            // - wait for startup
            await Task.Delay(TimeSpan.FromMilliseconds(100));
            // - raise event
            _mockMothra!.RaisePeerDiscovered(Encoding.UTF8.GetBytes("peer1"));
            // - wait for event to be handled
            await Task.Delay(TimeSpan.FromMilliseconds(100));
            // - finish
            await peeringWorker.StopAsync(CancellationToken.None);

            // assert
            _peerManager.Peers.Count.ShouldBe(1);
            
            Session session = _peerManager.Sessions["peer1"].First();
            session.Direction.ShouldBe(ConnectionDirection.Out);
            session.State.ShouldBe(SessionState.New);
            session.Peer.Id.ShouldBe("peer1");
            session.Peer.Status.ShouldBeNull();
        }
        
        [Test]
        public async Task BlocksByRangeRequestShouldCreateResponse()
        {
            // arrange
            Root root6 = new Root(Enumerable.Repeat((byte) 0x67, 32).ToArray());
            Root root4 = new Root(Enumerable.Repeat((byte) 0x45, 32).ToArray());
            Root root2 = new Root(Enumerable.Repeat((byte) 0x23, 32).ToArray());
            Root requestRoot = root6;

            SignedBeaconBlock block6 = new SignedBeaconBlock(new BeaconBlock(new Slot(6), root4, Root.Zero, BeaconBlockBody.Zero),
                BlsSignature.Zero);
            SignedBeaconBlock block4 = new SignedBeaconBlock(new BeaconBlock(new Slot(4), root2, Root.Zero, BeaconBlockBody.Zero),
                BlsSignature.Zero);
            SignedBeaconBlock block2 = new SignedBeaconBlock(new BeaconBlock(new Slot(2), Root.Zero, Root.Zero, BeaconBlockBody.Zero),
                BlsSignature.Zero);

            _mockForkChoice!.GetAncestorAsync(Arg.Any<IStore>(), Arg.Any<Root>(), Arg.Any<Slot>()).Returns(Root.Zero);
            _mockForkChoice.GetAncestorAsync(Arg.Any<IStore>(), Arg.Any<Root>(), new Slot(6)).Returns(root6);
            _mockForkChoice.GetAncestorAsync(Arg.Any<IStore>(), Arg.Any<Root>(), new Slot(5)).Returns(root4);
            _mockForkChoice.GetAncestorAsync(Arg.Any<IStore>(), Arg.Any<Root>(), new Slot(4)).Returns(root4);
            _mockForkChoice.GetAncestorAsync(Arg.Any<IStore>(), Arg.Any<Root>(), new Slot(3)).Returns(root2);

            _mockStore!.GetSignedBlockAsync(root6).Returns(block6);
            _mockStore.GetSignedBlockAsync(root4).Returns(block4);
            _mockStore.GetSignedBlockAsync(root2).Returns(block2);
                
            MothraPeeringWorker peeringWorker = new MothraPeeringWorker(
                _loggerFactory.CreateLogger<MothraPeeringWorker>(),
                _mockMothraConfigurationMonitor!,
                Substitute.For<IHostEnvironment>(),
                Substitute.For<IClientVersion>(),
                _mockStore,
                _mockMothra!,
                _dataDirectory!,
                _peerManager!,
                _peerDiscoveredProcessor!,
                _rpcPeeringStatusProcessor!,
                _rpcBeaconBlocksByRangeProcessor!,
                _signedBeaconBlockProcessor!
            );
        
            // act - start worker
            await peeringWorker.StartAsync(CancellationToken.None);
            await Task.Delay(TimeSpan.FromMilliseconds(100));
            // - request for 4 blocks: 3, 4, 5, 6
            BeaconBlocksByRange request = new BeaconBlocksByRange(
                requestRoot,
                new Slot(3),
                4,
                1);
            byte[] data = new Byte[Ssz.Ssz.BeaconBlocksByRangeLength];
            Ssz.Ssz.Encode(data, request);
            _mockMothra!.RaiseRpcReceived(
                Encoding.UTF8.GetBytes("/eth2/beacon_chain/req/beacon_blocks_by_range/1/"),
                0,
                Encoding.UTF8.GetBytes("peer1"),
                data);
            // - wait for event to be handled
            await Task.Delay(TimeSpan.FromMilliseconds(100));
            // - finish
            await peeringWorker.StopAsync(CancellationToken.None);

            // assert - should receive in slot order
            var receivedCalls = _mockNetworkPeering.ReceivedCalls().ToList();
            
            receivedCalls.Count.ShouldBe(2);
            
            receivedCalls[0].GetMethodInfo().Name.ShouldBe(nameof(_mockNetworkPeering.SendBlockAsync));
            SignedBeaconBlock response0 = receivedCalls[0].GetArguments()[1].ShouldBeOfType<SignedBeaconBlock>();
            response0.Message.Slot.ShouldBe(new Slot(4));
            
            SignedBeaconBlock response1 = receivedCalls[1].GetArguments()[1].ShouldBeOfType<SignedBeaconBlock>();
            response1.Message.Slot.ShouldBe(new Slot(6));
        }
    }
}