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
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nethermind.BeaconNode;
using Nethermind.BeaconNode.OApiClient;
using Nethermind.BeaconNode.Services;
using Nethermind.HonestValidator.Services;
using Nethermind.HonestValidator.Tests.Helpers;
using NSubstitute;
using Shouldly;
using ValidatorDuty = Nethermind.BeaconNode.OApiClient.ValidatorDuty;

namespace Nethermind.HonestValidator.Test
{
    [TestClass]
    public class HonestValidatorWorkerTest
    {
//        [TestMethod]
//        public Task WorkerSignsBlock()
//        {
//            // Arrange
//            IServiceCollection testServiceCollection = TestSystem.BuildTestServiceCollection();
//
//            IBeaconNodeOApiClient beaconNodeOApiClient = Substitute.For<IBeaconNodeOApiClient>();
//            beaconNodeOApiClient.VersionAsync(Arg.Any<CancellationToken>()).Returns("TESTVERSION");
//            beaconNodeOApiClient.TimeAsync(Arg.Any<CancellationToken>()).Returns(1_578_009_600uL);
//            ValidatorDuty validatorDuty = new Nethermind.BeaconNode.OApiClient.ValidatorDuty()
//            {
//                Validator_pubkey = new byte[0],
//                Attestation_slot = 6,
//                Attestation_shard = 0,
//                Block_proposal_slot = 1
//            };
//            List<Nethermind.BeaconNode.OApiClient.ValidatorDuty> validatorDuties =
//                new List<Nethermind.BeaconNode.OApiClient.ValidatorDuty>() {validatorDuty};
//            beaconNodeOApiClient
//                .DutiesAsync(Arg.Any<IEnumerable<byte[]>>(), Arg.Any<ulong?>(), Arg.Any<CancellationToken>())
//                .Returns(validatorDuties);
//
//            IBeaconNodeOApiClientFactory beaconNodeOApiClientFactory = Substitute.For<IBeaconNodeOApiClientFactory>();
//            beaconNodeOApiClientFactory.CreateClient(Arg.Any<string>()).Returns(beaconNodeOApiClient);
//            testServiceCollection.AddSingleton<IBeaconNodeOApiClientFactory>(beaconNodeOApiClientFactory);
//
//            // 1_578_009_600uL is 2020-01-03 00:00:00 +0
//            DateTimeOffset[] testTimes = new[]
//            {
//                // start time
//                new DateTimeOffset(2020, 01, 03, 0, 0, 01, 0, TimeSpan.Zero),
//                // check wait time against previous + 1 second.. will trigger immediately
//                new DateTimeOffset(2020, 01, 03, 0, 0, 02, 0, TimeSpan.Zero),
//
//                // start time of second loop, 600 ms in
//                new DateTimeOffset(2020, 01, 02, 23, 55, 01, 600, TimeSpan.Zero),
//                // start waiting at 750 ms, so should wait 250 ms
//                new DateTimeOffset(2020, 01, 02, 23, 55, 01, 750, TimeSpan.Zero),
//
//                // start time of third loop
//                new DateTimeOffset(2020, 01, 02, 23, 55, 02, 0, TimeSpan.Zero),
//                // when last it dequeued, the wait handle will be triggered (with cancellation token should exit immediately)
//                new DateTimeOffset(2020, 01, 02, 23, 55, 02, 500, TimeSpan.Zero),
//            };
//            FastTestClock fastTestClock = new FastTestClock(testTimes);
//            testServiceCollection.AddSingleton<IClock>(fastTestClock);
//
//            ServiceProvider testServiceProvider = testServiceCollection.BuildServiceProvider();
//            
//            // Act
//            IEnumerable<IHostedService> hostedServices = testServiceProvider.GetServices<IHostedService>();
//            BeaconNodeWorker worker = hostedServices.OfType<BeaconNodeWorker>().First();
//            
//            await worker.StartAsync(new CancellationToken());
//            bool signal = fastTestClock.CompleteWaitHandle.WaitOne(TimeSpan.FromSeconds(10));
//            await worker.StopAsync(new CancellationToken());
//            
//            // Assert
//            signal.ShouldBeTrue();
//            
//        }
    }
}