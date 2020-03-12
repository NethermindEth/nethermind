using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core2.Types;
using NUnit.Framework;
using Shouldly;

namespace Nethermind.BeaconNode.Peering.Test
{
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
            var peerSyncStatus = new PeerSyncStatus();
            peerSyncStatus.UpdateMostRecentSlot(new Slot(5));

            var startEvent = new ManualResetEventSlim();
            var random = new Random();
            var taskList = new List<Task>();
            for (int i = 0; i < 10; i++)
            {
                Slot slot = new Slot((ulong) random.Next(5, 10));
                Task task = Task.Run(() =>
                {
                    startEvent.Wait();
                    peerSyncStatus.UpdateMostRecentSlot(slot);
                });
                taskList.Add(task);
            }
            taskList.Add(Task.Run(() =>
            {
                Slot slot = new Slot((ulong) 50);
                startEvent.Wait();
                peerSyncStatus.UpdateMostRecentSlot(slot);
            }));
            for (int i = 0; i < 10; i++)
            {
                Slot slot = new Slot((ulong) random.Next(5, 10));
                Task task = Task.Run(() =>
                {
                    startEvent.Wait();
                    peerSyncStatus.UpdateMostRecentSlot(slot);
                });
                taskList.Add(task);
            }

            // act
            startEvent.Set();            
            Task.WaitAll(taskList.ToArray());
            
            // assert
            peerSyncStatus.HighestPeerSlot.ShouldBe(new Slot(50));
        }
    }
}