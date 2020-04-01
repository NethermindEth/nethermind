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
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using Nethermind.Core2;
using Nethermind.Core2.Configuration;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Nethermind.BeaconNode.Peering.Test
{
    [TestFixture]
    public class MothraPeeringWorkerTests
    {
        [Test]
        public async Task StartWorkerShouldStartMothra()
        {
            // arrange
            IOptionsMonitor<ConsoleLoggerOptions> mockLoggerOptionsMonitor = Substitute.For<IOptionsMonitor<ConsoleLoggerOptions>>();
            mockLoggerOptionsMonitor.CurrentValue.Returns(new ConsoleLoggerOptions()
            {
                Format = ConsoleLoggerFormat.Systemd,
                DisableColors = true,
                IncludeScopes = true,
                TimestampFormat = " HH':'mm':'sszz "
            });
            LoggerFactory loggerFactory = new LoggerFactory(new [] { new ConsoleLoggerProvider(mockLoggerOptionsMonitor) });
            
            MockMothra mockMothra = new MockMothra();
            // mockMothra.StartCalled += settings =>
            // {
            //     ThreadPool.QueueUserWorkItem(x =>
            //     {
            //         Thread.Sleep(TimeSpan.FromMilliseconds(100));
            //         byte[] peerUtf8 = Encoding.UTF8.GetBytes("peer1");
            //         mockMothra.RaisePeerDiscovered(peerUtf8);
            //     });
            // };
            
            IForkChoice mockForkChoice = Substitute.For<IForkChoice>();
            ISynchronizationManager mockSynchronizationManager = Substitute.For<ISynchronizationManager>();
            IStore mockStore = Substitute.For<IStore>();
            mockStore.IsInitialized.Returns(true);
            IOptionsMonitor<MothraConfiguration> mockMothraConfigurationMonitor = Substitute.For<IOptionsMonitor<MothraConfiguration>>();
            mockMothraConfigurationMonitor.CurrentValue.Returns(new MothraConfiguration());
            
            // TODO: Replace with MothraNetworkPeering and mockMothra.
            INetworkPeering mockNetworkPeering = Substitute.For<INetworkPeering>();
            
            PeerManager peerManager = new PeerManager(loggerFactory.CreateLogger<PeerManager>());
            
            MothraPeeringWorker peeringWorker = new MothraPeeringWorker(
                loggerFactory.CreateLogger<MothraPeeringWorker>(),
                mockMothraConfigurationMonitor,
                Substitute.For<IFileSystem>(),
                Substitute.For<IHostEnvironment>(),
                Substitute.For<IClientVersion>(),
                mockForkChoice,
                mockSynchronizationManager,
                mockStore,
                mockMothra,
                new DataDirectory("data"),
                peerManager
            );
        
            // act
            await peeringWorker.StartAsync(CancellationToken.None);
            await Task.Delay(TimeSpan.FromMilliseconds(100));
            await peeringWorker.StopAsync(CancellationToken.None);
            
            // assert
            mockMothra.StartCalls.Count.ShouldBe(1);
            // mockMothra.SendRpcResponseCalls.Count.ShouldBe(1);
            // Encoding.UTF8.GetString(mockMothra.SendRpcResponseCalls[0].peerUtf8).ShouldBe("peer1");
            // Encoding.UTF8.GetString(mockMothra.SendRpcResponseCalls[0].methodUtf8).ShouldBe("/eth2/beacon_chain/req/status/1/");
        }

    }
}