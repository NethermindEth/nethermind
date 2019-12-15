using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nethermind.BeaconNode.Services;
using Nethermind.BeaconNode.Storage;
using Nethermind.BeaconNode.Tests.Helpers;
using NSubstitute;
using Shouldly;

namespace Nethermind.BeaconNode.Tests
{
    [TestClass]
    public class BeaconNodeWorkerTest
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
            
            IStoreProvider mockStoreProvider = Substitute.For<IStoreProvider>();
            testServiceCollection.AddSingleton<IStoreProvider>(mockStoreProvider);

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
            mockStoreProvider.Received(4).TryGetStore(out IStore? store);
        }

    }
}
