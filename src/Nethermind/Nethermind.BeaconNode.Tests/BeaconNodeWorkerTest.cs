﻿using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cortex.BeaconNode.Configuration;
using Cortex.BeaconNode.Services;
using Cortex.BeaconNode.Storage;
using Cortex.BeaconNode.Tests.Helpers;
using Cortex.Containers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSubstitute;
using Shouldly;

namespace Cortex.BeaconNode.Tests
{
    [TestClass]
    public class BeaconNodeWorkerTest
    {
        [TestMethod]
        public async Task CanRunFastClockForWorkerTest()
        {
            // Arrange
            var testServiceCollection = TestSystem.BuildTestServiceCollection(useStore: true);

            testServiceCollection.AddHostedService<BeaconNodeWorker>();
            
            var testTimes = new[]
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
            var fastTestClock = new FastTestClock(testTimes);
            testServiceCollection.AddSingleton<IClock>(fastTestClock);
            
            var mockStoreProvider = Substitute.For<IStoreProvider>();
            testServiceCollection.AddSingleton<IStoreProvider>(mockStoreProvider);

            testServiceCollection.AddSingleton<IHostEnvironment>(Substitute.For<IHostEnvironment>());

            var testServiceProvider = testServiceCollection.BuildServiceProvider();

            // Act
            var hostedServices = testServiceProvider.GetServices<IHostedService>();
            var worker = hostedServices.OfType<BeaconNodeWorker>().First();
            await worker.StartAsync(new CancellationToken());

            var signal = fastTestClock.CompleteWaitHandle.WaitOne(TimeSpan.FromSeconds(10));

            await worker.StopAsync(new CancellationToken());

            // Assert
            signal.ShouldBeTrue();
            mockStoreProvider.GetStore().Received(4);
        }

    }
}
