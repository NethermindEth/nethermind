using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Types;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Nethermind.BeaconNode.Peering.Test
{
    [TestFixture]
    public class PeerSyncStatusTests
    {
        [SetUp]
        public void Setup()
        {
        }

        [Test]
        [Repeat(1000)]
        public void UpdateSlotShouldBeThreadSafe()
        {
            // arrange
            PeerManager peerManager = new PeerManager(Substitute.For<ILogger<PeerManager>>());
            peerManager.UpdateMostRecentSlot(new Slot(5));

            ManualResetEventSlim startEvent = new ManualResetEventSlim();
            Random random = new Random();
            List<Task> taskList = new List<Task>();
            for (int i = 0; i < 10; i++)
            {
                Slot slot = new Slot((ulong) random.Next(5, 10));
                Task task = Task.Run(() =>
                {
                    startEvent.Wait();
                    peerManager.UpdateMostRecentSlot(slot);
                });
                taskList.Add(task);
            }
            taskList.Add(Task.Run(() =>
            {
                Slot slot = new Slot((ulong) 50);
                startEvent.Wait();
                peerManager.UpdateMostRecentSlot(slot);
            }));
            for (int i = 0; i < 10; i++)
            {
                Slot slot = new Slot((ulong) random.Next(5, 10));
                Task task = Task.Run(() =>
                {
                    startEvent.Wait();
                    peerManager.UpdateMostRecentSlot(slot);
                });
                taskList.Add(task);
            }

            // act
            startEvent.Set();            
            Task.WaitAll(taskList.ToArray());
            
            // assert
            peerManager.HighestPeerSlot.ShouldBe(new Slot(50));
        }

        [Test]
        public void SlotInterlockedOnlyAffectsOneValue()
        {
            // arrange
            Slot slot = new Slot(10);
            Slot slot2 = slot;
            PendingAttestation slotContainer =
                new PendingAttestation(new BitArray(0), AttestationData.Zero, slot, ValidatorIndex.Zero);

            // act
            Slot comparand = new Slot(10);
            PendingAttestation containerToSourceUpdatedSlotFrom = new PendingAttestation(new BitArray(0),
                AttestationData.Zero, new Slot(20), ValidatorIndex.Zero);
            Slot original = Slot.InterlockedCompareExchange(ref slot, containerToSourceUpdatedSlotFrom.InclusionDelay, comparand);

            // NOTE: Doesn't make properties mutable, as you need the field ref (not a property), e.g. the following doesn't compile:
            // Example: Slot.InterlockedCompareExchange(ref slotContainer.InclusionDelay, slot, containerToSourceUpdatedSlotFrom.InclusionDelay, comparand);
            
            // assert
            slot.ShouldBe(new Slot(20));
            
            slot2.ShouldBe(new Slot(10));
            slotContainer.InclusionDelay.ShouldBe(new Slot(10));
            original.ShouldBe(new Slot(10));
        }

        // [Test]
        // public async Task StatusHandshakeOutgoing()
        // {
        //     // arrange
        //     IServiceCollection testServiceCollection = TestSystem.BuildTestServiceCollection();
        //     testServiceCollection.AddSingleton(Substitute.For<IHostEnvironment>());
        //     
        //     IMothraLibp2p mockMothra = Substitute.For<IMothraLibp2p>();
        //     mockMothra.When(x => x.Start(Arg.Any<MothraSettings>()))
        //         .Do(callInfo =>
        //         {
        //             ThreadPool.QueueUserWorkItem(x =>
        //             {
        //                 Thread.Sleep(TimeSpan.FromMilliseconds(100));
        //                 byte[] peerUtf8 = Encoding.UTF8.GetBytes("peer1");
        //                 mockMothra.PeerDiscovered += Raise.Event<PeerDiscoveredEventHandler>(peerUtf8);
        //             });
        //         });
        //     testServiceCollection.AddSingleton<IMothraLibp2p>(mockMothra);
        //     ServiceProvider testServiceProvider = testServiceCollection.BuildServiceProvider();
        //
        //     BeaconState state = TestState.PrepareTestState(testServiceProvider);
        //
        //     MothraPeeringWorker peeringWorker =
        //         testServiceProvider.GetServices<IHostedService>().OfType<MothraPeeringWorker>().First();
        //
        //     // act
        //     await peeringWorker.StartAsync(CancellationToken.None);
        //     await Task.Delay(TimeSpan.FromMilliseconds(1000));
        //     await peeringWorker.StopAsync(CancellationToken.None);
        //     
        //     // assert
        //     var receivedCalls = mockMothra.ReceivedCalls().ToList();
        //     receivedCalls.Count.ShouldBe(1);
        //     receivedCalls[0].GetMethodInfo().Name.ShouldBe(nameof(mockMothra.SendRpcRequest));
        //     
        // }
    }
}