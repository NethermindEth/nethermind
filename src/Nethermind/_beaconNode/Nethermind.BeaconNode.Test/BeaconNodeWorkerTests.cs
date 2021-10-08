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
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nethermind.BeaconNode.Services;
using Nethermind.BeaconNode.Storage;
using Nethermind.BeaconNode.Test.Helpers;
using Nethermind.Core2;
using NSubstitute;
using Shouldly;

namespace Nethermind.BeaconNode.Test
{
    [TestClass]
    public class BeaconNodeWorkerTests
    {
        [TestMethod]
        public async Task CanRunFastClockForWorkerTest()
        {
            // Arrange
            IServiceCollection testServiceCollection = TestSystem.BuildTestServiceCollection(useStore: true);

            testServiceCollection.AddHostedService<BeaconNodeWorker>();
            
            DateTimeOffset[] testTimes = new[]
            {
                // start time
                new DateTimeOffset(2020, 01, 02, 23, 55, 00, 0, TimeSpan.Zero),
                // check wait time against previous + 1 second.. will trigger immediately
                new DateTimeOffset(2020, 01, 02, 23, 55, 01, 0, TimeSpan.Zero),

                // start time of second loop
                new DateTimeOffset(2020, 01, 02, 23, 55, 01, 0, TimeSpan.Zero),
                // check 1ms left, so will wait
                new DateTimeOffset(2020, 01, 02, 23, 55, 01, 999, TimeSpan.Zero),

                // start time of third loop, 1ms in
                new DateTimeOffset(2020, 01, 02, 23, 55, 02, 1, TimeSpan.Zero),
                // should check against rounded second + 1 and trigger immediately
                new DateTimeOffset(2020, 01, 02, 23, 55, 03, 0, TimeSpan.Zero),

                // start time of fourth loop
                new DateTimeOffset(2020, 01, 02, 23, 55, 03, 0, TimeSpan.Zero),
                // when last it dequeued, the wait handle will be triggered
                new DateTimeOffset(2020, 01, 02, 23, 55, 03, 500, TimeSpan.Zero),
            };
            FastTestClock fastTestClock = new FastTestClock(testTimes);
            testServiceCollection.AddSingleton<IClock>(fastTestClock);
            
            IStore mockStore = Substitute.For<IStore>();
            testServiceCollection.AddSingleton<IStore>(mockStore);

            testServiceCollection.AddSingleton<IHostEnvironment>(Substitute.For<IHostEnvironment>());

            ServiceProvider testServiceProvider = testServiceCollection.BuildServiceProvider();

            // Act
            IEnumerable<IHostedService> hostedServices = testServiceProvider.GetServices<IHostedService>();
            BeaconNodeWorker worker = hostedServices.OfType<BeaconNodeWorker>().First();
            await worker.StartAsync(new CancellationToken());

            bool signal = fastTestClock.CompleteWaitHandle.WaitOne(TimeSpan.FromSeconds(10));

            await worker.StopAsync(new CancellationToken());

            // Assert
            signal.ShouldBeTrue();
            var receivedCalls = mockStore.ReceivedCalls().ToList();
            receivedCalls.Count(x => x.GetMethodInfo().Name == "get_IsInitialized").ShouldBe(4);
        }

        [TestMethod]
        [Ignore("Test is sensitive to timing and sometimes fails build.")]
        public async Task SleepTimeIsCorrectFromStartOfSecond()
        {
            // Arrange
            IServiceCollection testServiceCollection = TestSystem.BuildTestServiceCollection(useStore: true);

            testServiceCollection.AddHostedService<BeaconNodeWorker>();
            
            DateTimeOffset[] testTimes = new[]
            {
                // start time
                new DateTimeOffset(2020, 01, 02, 23, 55, 00, 0, TimeSpan.Zero),
                // check wait time against previous + 1 second.. will trigger immediately
                new DateTimeOffset(2020, 01, 02, 23, 55, 01, 0, TimeSpan.Zero),

                // start time of second loop, 600 ms in
                new DateTimeOffset(2020, 01, 02, 23, 55, 01, 600, TimeSpan.Zero),
                // start waiting at 750 ms, so should wait 250 ms
                new DateTimeOffset(2020, 01, 02, 23, 55, 01, 750, TimeSpan.Zero),

                // start time of third loop
                new DateTimeOffset(2020, 01, 02, 23, 55, 02, 0, TimeSpan.Zero),
                // when last it dequeued, the wait handle will be triggered (with cancellation token should exit immediately)
                new DateTimeOffset(2020, 01, 02, 23, 55, 02, 500, TimeSpan.Zero),
            };
            FastTestClock fastTestClock = new FastTestClock(testTimes);
            testServiceCollection.AddSingleton<IClock>(fastTestClock);
            
            IStore mockStore = Substitute.For<IStore>();
            testServiceCollection.AddSingleton<IStore>(mockStore);

            testServiceCollection.AddSingleton(Substitute.For<IHostEnvironment>());

            ServiceProvider testServiceProvider = testServiceCollection.BuildServiceProvider();

            // Act
            IEnumerable<IHostedService> hostedServices = testServiceProvider.GetServices<IHostedService>();
            BeaconNodeWorker worker = hostedServices.OfType<BeaconNodeWorker>().First();

            Stopwatch stopwatch = Stopwatch.StartNew();

            await worker.StartAsync(new CancellationToken());
            bool signal = fastTestClock.CompleteWaitHandle.WaitOne(TimeSpan.FromSeconds(10));
            await worker.StopAsync(new CancellationToken());
            
            stopwatch.Stop();

            // Assert
            signal.ShouldBeTrue();
            TimeSpan minTime = TimeSpan.FromMilliseconds(200);
            TimeSpan maxTime = TimeSpan.FromMilliseconds(350);
            stopwatch.Elapsed.ShouldBeInRange(minTime, maxTime);
        }
    }
}
