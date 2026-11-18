// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DotNetty.Buffers;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Threading;
using Nethermind.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.P2P.Subprotocols;
using Nethermind.Network.P2P.Subprotocols.Eth;
using Nethermind.Network.P2P.Utils;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test;

[TestFixture, NonParallelizable]
public class BackgroundTaskSchedulerWrapperTests
{
    private const int PeerLimit = 512;

    [Test]
    public async Task Running_work_remains_charged_until_completion()
    {
        HoldingScheduler scheduler = new();
        using HandlerHarness handler = new(scheduler, TestItem.PublicKeyA, "eth");
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using OwnedRequest running = new() { Gate = release.Task };
        Assert.That(handler.Schedule(3, running), Is.True);
        Task executing = scheduler.RunLast();
        await running.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            for (int i = 1; i < PeerLimit; i++) Assert.That(handler.Schedule(3, new OwnedRequest()), Is.True);
            using OwnedRequest rejected = new();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(handler.Schedule(3, rejected), Is.False);
                Assert.That(rejected.Buffer.ReferenceCount, Is.Zero);
            }
        }
        finally
        {
            release.TrySetResult();
            await executing;
            await scheduler.Drain();
        }
        using OwnedRequest accepted = new();
        Assert.That(handler.Schedule(3, accepted), Is.True);
        await scheduler.Drain();
        Assert.That(accepted.Executed, Is.True);
    }

    private static IEnumerable<int> FloodCapacities() => [2048, new Nethermind.Api.InitConfig().BackgroundTaskMaxNumber];

    [Test]
    public async Task Flooding_identities_leave_space_for_another_peer_on_the_real_scheduler([ValueSource(nameof(FloodCapacities))] int capacity)
    {
        IBranchProcessor processor = Substitute.For<IBranchProcessor>();
        await using BackgroundTaskScheduler scheduler = new(processor, Substitute.For<IChainHeadInfoProvider>(), 1, capacity, LimboLogs.Instance);
        processor.BlocksProcessing += Raise.EventWith(new BlocksProcessingEventArgs([]));
        PublicKey[] identities = TestItem.PublicKeys[..16];
        List<HandlerHarness> attackers = [];
        List<Task> disposed = [];
        using HandlerHarness honest = new(scheduler, TestItem.PublicKeys[16], "eth");
        using OwnedRequest honestWork = new();
        disposed.Add(honestWork.Disposed.Task);
        bool honestQueued = false;
        int totalAccepted = 0;
        try
        {
            foreach (PublicKey identity in identities)
            {
                HandlerHarness attacker = new(scheduler, identity, "eth");
                attackers.Add(attacker);
                int accepted = 0;
                for (int attempt = 0; attempt < 512; attempt++)
                {
                    OwnedRequest request = new();
                    disposed.Add(request.Disposed.Task);
                    if (attacker.Schedule(3, request)) accepted++;
                    else Assert.That(request.Buffer.ReferenceCount, Is.Zero);
                }
                totalAccepted += accepted;
                Assert.That(accepted, Is.EqualTo(capacity / 16 - 64),
                    "the ordinary share leaves both protected reply classes and other identities room");
                attacker.Session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
            }
            using (Assert.EnterMultipleScope())
            {
                Assert.That(attackers.Count, Is.EqualTo(16));
                Assert.That(totalAccepted, Is.EqualTo(16 * (capacity / 16 - 64)));
            }
            honestQueued = honest.Schedule(3, honestWork);
            Assert.That(honestQueued, Is.True);
        }
        finally
        {
            if (!honestQueued) honestWork.Dispose();
            processor.BranchProcessingCompleted += Raise.EventWith(new BranchProcessingCompletedEventArgs([], 0));
            await Task.WhenAll(disposed).WaitAsync(TimeSpan.FromSeconds(5));
            foreach (HandlerHarness attacker in attackers) attacker.Dispose();
        }
        honest.Session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
    }

    [Test]
    public async Task Existing_scheduler_deadline_cleans_payloads_and_releases_peer_quota([Values(0, 1, 2, 3)] int requestKind)
    {
        IBranchProcessor processor = Substitute.For<IBranchProcessor>();
        await using BackgroundTaskScheduler scheduler = new(processor, Substitute.For<IChainHeadInfoProvider>(), 1, 16, LimboLogs.Instance);
        using HandlerHarness handler = new(scheduler, TestItem.PublicKeyA, "eth");
        processor.BlocksProcessing += Raise.EventWith(new BlocksProcessingEventArgs([]));
        using OwnedRequest request = new();
        Assert.That(handler.Schedule(requestKind, request), Is.True);
        using OwnedRequest rejected = new();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(handler.Schedule(requestKind, rejected), Is.False);
            Assert.That(rejected.Buffer.ReferenceCount, Is.Zero);
        }
        await request.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(request.Buffer.ReferenceCount, Is.Zero);
            Assert.That(request.Canceled, Is.True);
            handler.Session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
        }
        processor.BranchProcessingCompleted += Raise.EventWith(new BranchProcessingCompletedEventArgs([], 0));
        TaskCompletionSource drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.That(scheduler.TryScheduleTask(default(DrainRequest), (_, _) =>
        {
            drained.TrySetResult();
            return Task.CompletedTask;
        }), Is.True);
        await drained.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using OwnedRequest accepted = new();
        Assert.That(handler.Schedule(requestKind, accepted), Is.True);
        await accepted.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(accepted.Buffer.ReferenceCount, Is.Zero);
            Assert.That(accepted.Executed, Is.True);
        }
    }

    [Test]
    public async Task Peer_work_quota_is_shared_across_protocols_and_preserves_other_peers([Values(0, 1, 2, 3)] int requestKind)
    {
        HoldingScheduler scheduler = new() { QueueCapacity = 32 };
        using HandlerHarness eth = new(scheduler, TestItem.PublicKeyA, "eth");
        using HandlerHarness snap = new(scheduler, TestItem.PublicKeyA, "snap", eth.Session);
        using HandlerHarness honest = new(scheduler, TestItem.PublicKeyB, "eth");
        List<OwnedRequest> requests = [];
        try
        {
            for (int i = 0; i < 2; i++)
            {
                OwnedRequest request = new();
                requests.Add(request);
                Assert.That((i % 2 == 0 ? eth : snap).Schedule(requestKind, request), Is.True);
            }
            OwnedRequest rejected = new();
            requests.Add(rejected);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(snap.Schedule(requestKind, rejected), Is.False);
                Assert.That(rejected.Buffer.ReferenceCount, Is.Zero);
            }
            eth.Session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());

            OwnedRequest accepted = new();
            requests.Add(accepted);
            Assert.That(honest.Schedule(requestKind, accepted), Is.True);
            await scheduler.RunLast();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(accepted.Executed, Is.True);
                Assert.That(accepted.Buffer.ReferenceCount, Is.Zero);
            }
        }
        finally
        {
            await scheduler.Drain();
            foreach (OwnedRequest request in requests) request.Dispose();
        }
    }

    [Test]
    public async Task Closed_session_keeps_queued_work_charged_to_reconnecting_identity()
    {
        HoldingScheduler scheduler = new();
        using HandlerHarness old = new(scheduler, TestItem.PublicKeyA, "eth");
        using HandlerHarness replacement = new(scheduler, TestItem.PublicKeyA, "snap");
        List<OwnedRequest> requests = [];
        try
        {
            for (int i = 0; i < PeerLimit; i++)
            {
                OwnedRequest request = new();
                requests.Add(request);
                Assert.That(old.Schedule(3, request), Is.True);
            }
            old.Session.IsClosing.Returns(true);
            using OwnedRequest rejected = new();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(replacement.Schedule(3, rejected), Is.False);
                Assert.That(rejected.Buffer.ReferenceCount, Is.Zero);
            }
            await scheduler.Drain();
            Assert.That(requests.TrueForAll(static request => request.Canceled && request.Buffer.ReferenceCount == 0), Is.True);
            using OwnedRequest accepted = new();
            Assert.That(replacement.Schedule(3, accepted), Is.True);
            await scheduler.Drain();
            Assert.That(accepted.Executed, Is.True);
        }
        finally
        {
            await scheduler.Drain();
            foreach (OwnedRequest request in requests) request.Dispose();
        }
    }

    [Test]
    public async Task Completion_releases_work_quota_and_owned_payload([Values(0, 1, 2, 3)] int requestKind,
        [Values(0, 1, 2)] int completion)
    {
        HoldingScheduler scheduler = new() { QueueCapacity = 16 };
        using HandlerHarness handler = new(scheduler, TestItem.PublicKeyA, "eth");
        for (int round = 0; round < 2; round++)
        {
            using OwnedRequest request = new() { Fail = completion == 1 };
            Assert.That(handler.Schedule(requestKind, request), Is.True);
            await scheduler.Drain(new CancellationToken(completion == 2));
            Assert.That(request.Buffer.ReferenceCount, Is.Zero);
        }
    }

    [Test]
    public async Task Scheduler_rejection_returns_quota_and_payload([Values(0, 1, 2, 3)] int requestKind, [Values] bool throws)
    {
        HoldingScheduler scheduler = new() { Reject = true, Throws = throws };
        using HandlerHarness handler = new(scheduler, TestItem.PublicKeyA, "eth");
        for (int i = 0; i < PeerLimit + 1; i++)
        {
            using OwnedRequest rejected = new();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(handler.Schedule(requestKind, rejected), Is.False);
                Assert.That(rejected.Buffer.ReferenceCount, Is.Zero);
            }
        }
        scheduler.Reject = false;
        scheduler.Throws = false;
        using OwnedRequest accepted = new();
        Assert.That(handler.Schedule(requestKind, accepted), Is.True);
        await scheduler.Drain();
        Assert.That(accepted.Executed, Is.True);
    }

    /// <remarks>
    /// An unknown queue of capacity Q can be filled by as few as ceil(Q / 576) identities when protected replies are available.
    /// </remarks>
    [Test]
    public void Unknown_scheduler_capacity_accepted_residual_has_bounded_identity_work()
    {
        PeerWorkQuota quota = new(null);
        List<PeerWorkQuota.Reservation> reservations = [];
        try
        {
            foreach (PeerWorkQuota.WorkKind kind in new[] { PeerWorkQuota.WorkKind.Ordinary, PeerWorkQuota.WorkKind.PooledReply, PeerWorkQuota.WorkKind.CellsReply })
            {
                int limit = kind == PeerWorkQuota.WorkKind.Ordinary ? 512 : 32;
                for (int index = 0; index < limit; index++)
                {
                    Assert.That(quota.TryReserve(TestItem.PublicKeyA, kind, null, out PeerWorkQuota.Reservation? reservation), Is.True);
                    reservations.Add(reservation!);
                }
                Assert.That(quota.TryReserve(TestItem.PublicKeyA, kind, null, out _), Is.False);
            }
            Assert.That(reservations.Count, Is.EqualTo(576));
        }
        finally { foreach (PeerWorkQuota.Reservation reservation in reservations) reservation.Dispose(); }
    }

    [Test]
    public void Production_capacity_preserves_thirteen_serving_classes_and_both_reply_reserves(
        [Values(7680, 8192, 9216)] int capacity)
    {
        PeerWorkQuota quota = new(capacity);
        List<PeerWorkQuota.Reservation> reservations = [];
        try
        {
            for (int requestClass = 0; requestClass < 13; requestClass++)
            {
                (string Protocol, int PacketType) key = requestClass < 8 ? ("eth", requestClass) : ("snap", requestClass - 8);
                for (int request = 0; request < 32; request++) Reserve(PeerWorkQuota.WorkKind.Ordinary, key);
                Assert.That(quota.TryReserve(TestItem.PublicKeyA, PeerWorkQuota.WorkKind.Ordinary, key, out _), Is.False);
            }
            int ordinary = capacity / 16 - 64;
            for (int index = 416; index < ordinary; index++) Reserve(PeerWorkQuota.WorkKind.Ordinary, null);
            Assert.That(quota.TryReserve(TestItem.PublicKeyA, PeerWorkQuota.WorkKind.Ordinary, null, out _), Is.False);
            for (int index = 0; index < 32; index++)
            {
                Reserve(PeerWorkQuota.WorkKind.PooledReply, null);
                Reserve(PeerWorkQuota.WorkKind.CellsReply, null);
            }
            using (Assert.EnterMultipleScope())
            {
                Assert.That(quota.TryReserve(TestItem.PublicKeyA, PeerWorkQuota.WorkKind.PooledReply, null, out _), Is.False);
                Assert.That(quota.TryReserve(TestItem.PublicKeyA, PeerWorkQuota.WorkKind.CellsReply, null, out _), Is.False);
                Assert.That(reservations.Count * 15, Is.LessThan(capacity));
            }
        }
        finally
        {
            foreach (PeerWorkQuota.Reservation reservation in reservations) reservation.Dispose();
        }

        void Reserve(PeerWorkQuota.WorkKind kind, (string Protocol, int PacketType)? key)
        {
            Assert.That(quota.TryReserve(TestItem.PublicKeyA, kind, key, out PeerWorkQuota.Reservation? reservation), Is.True);
            reservations.Add(reservation!);
        }
    }

    [Test]
    public async Task A_retention_step_resuming_after_the_final_release_cannot_resurrect_the_slot()
    {
        PeerWorkQuota quota = new(16);
        Assert.That(quota.TryReserve(TestItem.PublicKeyA, PeerWorkQuota.WorkKind.Ordinary, null,
            out PeerWorkQuota.Reservation? original), Is.True);
        object shared = typeof(PeerWorkQuota.Reservation).GetField("_shared", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(original)!;
        Action resumeRetention = shared.GetType().GetMethod("Retain", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Action>(shared);
        TaskCompletionSource finalRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task retaining = Task.Run(async () =>
        {
            await finalRelease.Task;
            Assert.That(() => resumeRetention(), Throws.TypeOf<ObjectDisposedException>());
        });
        try
        {
            original!.Dispose();
            finalRelease.TrySetResult();
            await retaining.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(quota.TryReserve(TestItem.PublicKeyA, PeerWorkQuota.WorkKind.Ordinary, null,
                out PeerWorkQuota.Reservation? replacement), Is.True);
            replacement!.Dispose();
        }
        finally
        {
            original!.Dispose();
            finalRelease.TrySetResult();
            await retaining;
        }
    }

    [Test]
    public void Retaining_a_reservation_racing_disposal_never_resurrects_a_released_quota_slot()
    {
        PeerWorkQuota quota = new(16);
        for (int attempt = 0; attempt < 1000; attempt++)
        {
            Assert.That(quota.TryReserve(TestItem.PublicKeyA, PeerWorkQuota.WorkKind.Ordinary, null,
                out PeerWorkQuota.Reservation? original), Is.True);
            PeerWorkQuota.Reservation? retained = null;
            Parallel.Invoke(() =>
            {
                try { retained = original!.Retain(); }
                catch (ObjectDisposedException) { }
            }, () => original!.Dispose());
            try
            {
                bool recovered = quota.TryReserve(TestItem.PublicKeyA, PeerWorkQuota.WorkKind.Ordinary, null,
                    out PeerWorkQuota.Reservation? replacement);
                using (replacement) Assert.That(recovered, Is.EqualTo(retained is null));
            }
            finally { retained?.Dispose(); }
            Assert.Throws<ObjectDisposedException>(() => original!.Retain());
            original!.Dispose();
            Assert.That(quota.TryReserve(TestItem.PublicKeyA, PeerWorkQuota.WorkKind.Ordinary, null,
                out PeerWorkQuota.Reservation? next), Is.True);
            next!.Dispose();
        }
    }

    [Test]
    public async Task Local_serving_faults_release_work_without_penalizing_the_peer(
        [Values(0, 1, 2, 3)] int requestKind, [Values(0, 1, 2, 3, 4, 5)] int fault)
    {
        HoldingScheduler scheduler = new() { QueueCapacity = 16 };
        using HandlerHarness handler = new(scheduler, TestItem.PublicKeyA, "eth");
        using OwnedRequest failed = new()
        {
            Failure = fault switch
            {
                0 => new InvalidOperationException("local database read failed"),
                1 => new RlpReaderException("local database value is malformed"),
                2 => new RlpLimitException("local database value exceeds the read limit"),
                3 => new EthSyncException("local sync service is unavailable"),
                4 => new SubprotocolException("local callback cannot fulfill the request"),
                _ => new InvalidDataException("local database record is invalid")
            }
        };
        Assert.That(handler.Schedule(requestKind, failed, serving: true), Is.True);
        await scheduler.Drain();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(failed.Buffer.ReferenceCount, Is.Zero);
            handler.Session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
        }
        using OwnedRequest honest = new();
        Assert.That(handler.Schedule(requestKind, honest, serving: true), Is.True);
        await scheduler.Drain();
        Assert.That(honest.Executed, Is.True);
    }

    [Test]
    public void Rescheduling_without_an_owned_reservation_preserves_caller_payload([Values] bool wrongType)
    {
        HoldingScheduler scheduler = new() { QueueCapacity = 16 };
        using HandlerHarness handler = new(scheduler, TestItem.PublicKeyA, "eth");
        using OwnedRequest request = new();
        IDisposable? reservation = wrongType ? request : null;
        Assert.That(handler.Reschedule(request, reservation), Is.False);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(request.Buffer.ReferenceCount, Is.EqualTo(1));
            Assert.That(request.Started.Task.IsCompleted, Is.False);
        }
    }

    [Test]
    public void Network_reservations_leave_scheduler_headroom_and_release_subnet_quota()
    {
        PeerWorkQuota quota = new(16);
        NodeFilter.IpSubnetKey subnet = NodeFilter.IpSubnetKey.DefaultKey(System.Net.IPAddress.Loopback);
        List<PeerWorkQuota.Reservation> held = [];
        try
        {
            for (byte i = 1; i <= 15; i++)
            {
                byte[] bytes = new byte[64];
                bytes[^1] = i;
                PublicKey id = new(bytes);
                if (i == 5)
                    Assert.That(quota.TryReserve(id, PeerWorkQuota.WorkKind.Ordinary, null, out _, subnet), Is.False);
                Assert.That(quota.TryReserve(id, PeerWorkQuota.WorkKind.Ordinary, null, out PeerWorkQuota.Reservation? slot,
                    i <= 4 ? subnet : null), Is.True);
                held.Add(slot!);
            }
            Assert.That(quota.TryReserve(TestItem.PublicKeyA, PeerWorkQuota.WorkKind.Ordinary, null, out _), Is.False);
            held[0].Dispose();
            Assert.That(quota.TryReserve(TestItem.PublicKeyA, PeerWorkQuota.WorkKind.Ordinary, null,
                out PeerWorkQuota.Reservation? replacement, subnet), Is.True);
            replacement!.Dispose();
        }
        finally
        {
            foreach (PeerWorkQuota.Reservation slot in held) slot.Dispose();
        }
    }

    private static IEnumerable<TestCaseData> NeutralFaultCases()
    {
        for (int kind = 0; kind < 5; kind++)
        {
            for (int fault = 0; fault < 8; fault++)
            {
                if (kind >= 2 && fault is 3 or 4) continue;
                yield return new TestCaseData(kind, fault);
            }
        }
    }

    [TestCaseSource(nameof(NeutralFaultCases))]
    public async Task Neutral_faults_preserve_exact_capacity_and_release_every_admitted_request(int kind, int fault)
    {
        HoldingScheduler scheduler = new() { QueueCapacity = 8192 };
        using HandlerHarness handler = new(scheduler, TestItem.PublicKeyA, "eth");
        int limit = kind == 4 ? 448 : 32;
        Exception? failure = fault switch
        {
            0 => new InvalidOperationException("local database read failed"),
            1 => new RlpReaderException("local database value is malformed"),
            2 => new RlpLimitException("local database value exceeds the read limit"),
            3 => new EthSyncException("local sync service is unavailable"),
            4 => new SubprotocolException("local callback cannot fulfill the request"),
            5 => new InvalidDataException("local database record is invalid"),
            6 => new OperationCanceledException("local operation was cancelled"),
            _ => null
        };
        List<OwnedRequest> requests = [];
        try
        {
            for (int round = 0; round < 2; round++)
            {
                for (int index = 0; index < limit; index++)
                {
                    OwnedRequest request = new() { Failure = round == 0 ? failure : null };
                    requests.Add(request);
                    Assert.That(handler.ScheduleClassified(kind, request), Is.True);
                }
                using OwnedRequest rejected = new();
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(handler.ScheduleClassified(kind, rejected), Is.False);
                    Assert.That(rejected.Buffer.ReferenceCount, Is.Zero);
                }
                await scheduler.Drain(new CancellationToken(round == 0 && fault == 7));
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(requests.TrueForAll(static request => request.Buffer.ReferenceCount == 0), Is.True);
                    Assert.That(requests.GetRange(round * limit, limit).TrueForAll(request =>
                        round == 0 ? !request.Executed && request.Canceled == (fault == 7) : request.Executed), Is.True);
                    handler.Session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
                }
            }
        }
        finally
        {
            await scheduler.Drain();
            foreach (OwnedRequest request in requests) request.Dispose();
        }
    }

    [Test]
    public async Task Rescheduled_suffix_shares_the_original_charge_during_competing_admission()
    {
        HoldingScheduler scheduler = new() { QueueCapacity = 16 };
        using HandlerHarness handler = new(scheduler, TestItem.PublicKeyA, "eth");
        using OwnedRequest originalPayload = new();
        using OwnedRequest suffix = new();
        using OwnedRequest competing = new();
        IDisposable? original = null;
        Assert.That(handler.ScheduleWithReservation(originalPayload, reservation => original = reservation), Is.True);
        FieldInfo shared = typeof(PeerWorkQuota.Reservation).GetField("_shared", BindingFlags.Instance | BindingFlags.NonPublic)!;
        using ManualResetEventSlim binding = new();
        using ManualResetEventSlim competed = new();
        Task competitor = Task.Run(() =>
        {
            Assert.That(binding.Wait(TimeSpan.FromSeconds(5)), Is.True);
            try { Assert.That(handler.Schedule(3, competing), Is.False); }
            finally { competed.Set(); }
        });
        try
        {
            Assert.That(handler.Reschedule(suffix, original, inspectReservation: retained =>
            {
                Assert.That(shared.GetValue(retained), Is.SameAs(shared.GetValue(original)));
                using PeerWorkQuota.Reservation witness = ((PeerWorkQuota.Reservation)original!).Retain();
                binding.Set();
                Assert.That(competed.Wait(TimeSpan.FromSeconds(5)), Is.True);
            }), Is.True);
            await competitor;
            using OwnedRequest beforeDrain = new();
            Assert.That(handler.Schedule(3, beforeDrain), Is.False);
            await scheduler.Drain();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(originalPayload.Executed, Is.True);
                Assert.That(suffix.Executed, Is.True);
                handler.Session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
            }
            using OwnedRequest recovered = new();
            Assert.That(handler.Schedule(3, recovered), Is.True);
            await scheduler.Drain();
            Assert.That(recovered.Executed, Is.True);
        }
        finally
        {
            binding.Set();
            competed.Set();
            await competitor;
            await scheduler.Drain();
        }
    }

    [Test]
    public async Task Failed_rescheduling_retains_the_original_charge_and_caller_payload(
        [Values(0, 1, 2, 3)] int failure)
    {
        HoldingScheduler scheduler = new() { QueueCapacity = 16 };
        using HandlerHarness handler = new(scheduler, TestItem.PublicKeyA, "eth");
        IDisposable? original = null;
        using OwnedRequest first = new();
        Assert.That(handler.ScheduleWithReservation(first, reservation => original = reservation), Is.True);
        using OwnedRequest suffix = new();
        scheduler.Reject = failure == 0;
        scheduler.Throws = failure == 1;
        handler.Session.IsClosing.Returns(failure == 3);
        try
        {
            Assert.That(handler.Reschedule(suffix, original, binderThrows: failure == 2), Is.False);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(suffix.Buffer.ReferenceCount, Is.EqualTo(1));
                Assert.That(suffix.Started.Task.IsCompleted, Is.False);
            }
            scheduler.Reject = false;
            scheduler.Throws = false;
            handler.Session.IsClosing.Returns(false);
            using OwnedRequest competing = new();
            Assert.That(handler.Schedule(3, competing), Is.False, "The queued original still owns the only ordinary slot");
            await scheduler.Drain();
            Assert.That(handler.Schedule(3, suffix), Is.True);
            await scheduler.Drain();
            Assert.That(suffix.Executed, Is.True);
        }
        finally
        {
            scheduler.Reject = false;
            scheduler.Throws = false;
            handler.Session.IsClosing.Returns(false);
            await scheduler.Drain();
        }
    }

    [Test]
    public async Task Reservation_binding_failure_disposes_payload_and_releases_the_slot()
    {
        HoldingScheduler scheduler = new() { QueueCapacity = 16 };
        using HandlerHarness handler = new(scheduler, TestItem.PublicKeyA, "eth");
        using OwnedRequest failed = new();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(handler.ScheduleWithReservation(failed, _ => throw new InvalidOperationException("binding failed")), Is.False);
            Assert.That(failed.Buffer.ReferenceCount, Is.Zero);
        }
        using OwnedRequest honest = new();
        Assert.That(handler.Schedule(3, honest), Is.True);
        await scheduler.Drain();
        Assert.That(honest.Executed, Is.True);
    }

    internal sealed class HandlerHarness : IDisposable
    {
        public ISession Session { get; }
        private readonly ProtocolHandlerBase _handler;
        private readonly BackgroundTaskSchedulerWrapper _wrapper;

        public HandlerHarness(IBackgroundTaskScheduler scheduler, PublicKey id, string protocol, ISession? session = null)
        {
            Session = session ?? Substitute.For<ISession>();
            Session.Node.Returns(new Node(id, "1.2.3.4", 30303));
            _handler = Substitute.For<ProtocolHandlerBase>(Session, Substitute.For<INodeStatsManager>(),
                Substitute.For<IMessageSerializationService>(), scheduler, LimboLogs.Instance, TimeProvider.System);
            _handler.ProtocolCode.Returns(protocol);
            _wrapper = new BackgroundTaskSchedulerWrapper(_handler, scheduler, TimeProvider.System);
        }

        public bool Schedule(int kind, OwnedRequest request, bool serving = false) => kind switch
        {
            0 => _wrapper.TryScheduleSyncServe(request, static (request, token) => request.Execute(token).AsTask()),
            1 => _wrapper.TryScheduleSyncServe(request, static (request, token) => request.Execute(token)),
            2 => _wrapper.TryScheduleSyncServe<ProtocolHandlerBase, OwnedRequest, PongMessage, TypedRequestHandler>(_handler, request),
            _ => _wrapper.TryScheduleBackgroundTask(request, static async (request, token) => { await request.Execute(token); }, serving: serving)
        };

        public bool ScheduleWithReservation(OwnedRequest request, Action<IDisposable?> capture)
            => _wrapper.TryScheduleBackgroundTask(request, static async (request, token) => { await request.Execute(token); },
                bindReservation: (payload, reservation) =>
                {
                    capture(reservation);
                    return payload;
                });

        public bool ScheduleClassified(int kind, OwnedRequest request) => kind switch
        {
            0 => _wrapper.TryScheduleSyncServe(new OwnedMessage(request), static (message, token) => message.Request.Execute(token).AsTask()),
            1 => _wrapper.TryScheduleBackgroundTask(new OwnedMessage(request), static async (message, token) =>
                { await message.Request.Execute(token); }, serving: true),
            _ => _wrapper.TryScheduleBackgroundTask(request, static async (payload, token) =>
                { await payload.Execute(token); }, solicited: kind is 2 or 3, cellsReply: kind == 3)
        };

        public bool Reschedule(OwnedRequest request, IDisposable? reservation, bool binderThrows = false,
            Action<IDisposable?>? inspectReservation = null)
            => _wrapper.TryRescheduleBackgroundTask(request, static async (request, token) => { await request.Execute(token); },
                reservation, (payload, retained) =>
                {
                    if (binderThrows) throw new InvalidOperationException("binding failed");
                    inspectReservation?.Invoke(retained);
                    return payload;
                });

        public void Dispose() => _handler.Dispose();
    }

    private readonly struct TypedRequestHandler : ISyncServeRequestHandler<ProtocolHandlerBase, OwnedRequest, PongMessage>
    {
        public static Task<PongMessage> Execute(ProtocolHandlerBase handler, OwnedRequest request, CancellationToken cancellationToken) =>
            request.Execute(cancellationToken).AsTask();
    }

    private readonly struct DrainRequest : IBackgroundTaskRequest<DrainRequest>;

    private sealed class OwnedMessage(OwnedRequest request) : P2PMessage
    {
        public OwnedRequest Request { get; } = request;
        public override int PacketType => 3;
        public override string Protocol => "eth";
        public override void Dispose() => Request.Dispose();
    }

    internal sealed class OwnedRequest : IDisposable
    {
        public IByteBuffer Buffer { get; } = PooledByteBufferAllocator.Default.Buffer(64);
        public bool Fail { get; init; }
        public Exception? Failure { get; init; }
        public Task? Gate { get; init; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Executed { get; private set; }
        public bool Canceled { get; private set; }
        private int _disposed;

        public async ValueTask<PongMessage> Execute(CancellationToken token)
        {
            using (this)
            {
                Started.TrySetResult();
                Canceled = token.IsCancellationRequested;
                token.ThrowIfCancellationRequested();
                if (Gate is not null) await Gate.WaitAsync(token);
                if (Failure is not null) throw Failure;
                if (Fail) throw new InvalidOperationException("injected work failure");
                Executed = true;
                return PongMessage.Instance;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Buffer.Release();
                Disposed.TrySetResult();
            }
        }
    }

    internal sealed class HoldingScheduler : IBackgroundTaskScheduler
    {
        public int? QueueCapacity { get; init; }
        private readonly List<Func<CancellationToken, Task>> _work = [];
        public bool Reject { get; set; }
        public bool Throws { get; set; }

        public bool TryScheduleTask<TReq>(TReq request, Func<TReq, CancellationToken, Task> fulfillFunc, TimeSpan? timeout = null)
            where TReq : notnull, IBackgroundTaskRequest<TReq>
        {
            if (Throws) throw new InvalidOperationException("injected scheduler failure");
            if (Reject) return false;
            _work.Add(token => fulfillFunc(request, token));
            return true;
        }

        public async Task RunLast(CancellationToken token = default)
        {
            Func<CancellationToken, Task> work = _work[^1];
            _work.RemoveAt(_work.Count - 1);
            await work(token);
        }

        public async Task Drain(CancellationToken token = default)
        {
            while (_work.Count != 0) await RunLast(token);
        }
    }

    public enum LegacyReplyShape { Honest, OversizedUnmatched, OversizedMixed }

    [Test]
    public void Legacy_reply_provenance_is_bounded_by_retained_request_hashes([Values] LegacyReplyShape shape)
    {
        ManualTimeProvider clock = new();
        PeerWorkQuota quota = new(8192);
        using LegacyResponseCredits credits = new(clock);
        for (int request = 0; request < 32; request++)
        {
            ValueHash256[] hashes = new ValueHash256[256];
            RetryRequestToken<ValueHash256>[] tokens = new RetryRequestToken<ValueHash256>[256];
            for (int index = 0; index < hashes.Length; index++)
            {
                hashes[index] = new Hash256((request * 256 + index + 1).ToString("X64")).ValueHash256;
                tokens[index] = new(hashes[index], request + 1, 1, long.MaxValue);
            }
            Assert.That(quota.TryReserve(TestItem.PublicKeyA, PeerWorkQuota.WorkKind.PooledReply, null,
                out PeerWorkQuota.Reservation? reservation), Is.True);
            using (reservation) Assert.That(credits.Reserve(reservation, hashes, tokens), Is.Not.Null);
        }
        Assert.That(quota.TryReserve(TestItem.PublicKeyA, PeerWorkQuota.WorkKind.PooledReply, null, out _), Is.False);
        if (shape != LegacyReplyShape.Honest)
        {
            Transaction[] oversized = new Transaction[16384];
            for (int index = 0; index < oversized.Length; index++)
                oversized[index] = new() { Hash = new Hash256((10000 + index).ToString("X64")) };
            if (shape == LegacyReplyShape.OversizedMixed) oversized[0].Hash = new Hash256(1.ToString("X64"));
            // No retained request has more than 256 hashes, so an oversized reply takes no request's credit.
            using (Assert.EnterMultipleScope())
            {
                Assert.That(credits.TryClaim(oversized, out PeerWorkQuota.Reservation? refused,
                    out PooledTransactionRequests.ResponseContext? unclaimed), Is.False);
                Assert.That(refused, Is.Null);
                Assert.That(unclaimed, Is.Null);
            }
            Assert.That(quota.TryReserve(TestItem.PublicKeyA, PeerWorkQuota.WorkKind.PooledReply, null, out _), Is.False);
        }
        Transaction[] returned = new Transaction[256];
        for (int index = 0; index < returned.Length; index++)
            returned[index] = new() { Hash = new Hash256((index + 1).ToString("X64")) };
        Assert.That(credits.TryClaim(returned, out PeerWorkQuota.Reservation? claimed,
            out PooledTransactionRequests.ResponseContext? context), Is.True);
        using (claimed)
        using (context)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(context!.Tokens.Count, Is.EqualTo(returned.Length));
                Assert.That(context.GetToken(returned.Length - 1).Generation, Is.EqualTo(1));
            }
            Assert.That(quota.TryReserve(TestItem.PublicKeyA, PeerWorkQuota.WorkKind.PooledReply, null, out _), Is.False);
            for (int index = 0; index < returned.Length; index++)
                Assert.That(context!.GetToken(index).ResourceId, Is.EqualTo(returned[index].Hash!.ValueHash256));
        }
        Assert.That(quota.TryReserve(TestItem.PublicKeyA, PeerWorkQuota.WorkKind.PooledReply, null,
            out PeerWorkQuota.Reservation? recovered), Is.True);
        using (recovered) Assert.That(quota.TryReserve(TestItem.PublicKeyA, PeerWorkQuota.WorkKind.PooledReply, null, out _), Is.False);
    }

    [Test]
    public void Oversized_legacy_reservation_does_not_retain_reply_capacity([Values] bool oversizedTokens)
    {
        ManualTimeProvider clock = new();
        PeerWorkQuota quota = new(8192);
        using LegacyResponseCredits credits = new(clock);
        Assert.That(quota.TryReserve(TestItem.PublicKeyA, PeerWorkQuota.WorkKind.PooledReply, null,
            out PeerWorkQuota.Reservation? reservation), Is.True);
        using (reservation)
            Assert.That(credits.Reserve(reservation, new ValueHash256[oversizedTokens ? 256 : 257],
                new RetryRequestToken<ValueHash256>[oversizedTokens ? 257 : 0]), Is.Null);
        List<PeerWorkQuota.Reservation> recovered = [];
        try
        {
            for (int index = 0; index < 32; index++)
            {
                Assert.That(quota.TryReserve(TestItem.PublicKeyA, PeerWorkQuota.WorkKind.PooledReply, null,
                    out PeerWorkQuota.Reservation? restored), Is.True);
                recovered.Add(restored!);
            }
            Assert.That(quota.TryReserve(TestItem.PublicKeyA, PeerWorkQuota.WorkKind.PooledReply, null, out _), Is.False);
        }
        finally { foreach (PeerWorkQuota.Reservation restored in recovered) restored.Dispose(); }
    }

    /// <remarks>Ordinary requests and validation continue. One retained same-hash request suppresses renewal until its original ten-second expiry.</remarks>
    [Test]
    public void accepted_residual_Completed_legacy_evidence_makes_a_repeated_hash_ambiguous_until_expiry([Values] bool claimed)
    {
        ManualTimeProvider clock = new();
        using LegacyResponseCredits credits = new(clock);
        ValueHash256 hash = TestItem.KeccakA.ValueHash256;
        RetryRequestToken<ValueHash256> first = new(hash, 1, 1, long.MaxValue);
        RetryRequestToken<ValueHash256> second = new(hash, 2, 1, long.MaxValue);
        PooledTransactionRequests.ResponseContext? initial;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(credits.Reserve(hashes: [hash], tokens: [first]), Is.Not.Null);
            Assert.That(credits.TryClaim([], out _, out initial), Is.True);
        }
        if (!claimed) initial!.Dispose();
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.That(credits.Reserve(hashes: [hash], tokens: [second]), Is.Not.Null);
        Transaction tx = new() { Hash = hash.ToHash256() };
        Assert.That(credits.TryClaim([tx], out _, out PooledTransactionRequests.ResponseContext? response), Is.True);
        using (response) Assert.That(response!.Tokens[0].Generation, Is.Zero);
        initial!.Dispose();
        clock.Advance(Timeouts.Eth);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(credits.Reserve(hashes: [hash], tokens: [second]), Is.Not.Null);
            Assert.That(credits.TryClaim([tx], out _, out response), Is.True);
        }
        using (response) Assert.That(response!.Tokens[0], Is.EqualTo(second));
    }

    /// <remarks>Completing more than 32 requests within ten seconds can evict evidence and suppress renewal through the maximum evicted expiry. Ordinary sending and validation continue.</remarks>
    [Test]
    public void accepted_residual_Legacy_evidence_eviction_keeps_the_maximum_original_horizon([Values] bool atMaximumExpiry)
    {
        ManualTimeProvider clock = new();
        using LegacyResponseCredits credits = new(clock);
        ValueHash256 oldest = TestItem.KeccakA.ValueHash256;
        Assert.That(credits.Reserve(hashes: [oldest]), Is.Not.Null);
        clock.Advance(TimeSpan.FromSeconds(2));
        ValueHash256 next = TestItem.KeccakB.ValueHash256;
        Assert.That(credits.Reserve(hashes: [next]), Is.Not.Null);
        Complete(next);
        clock.Advance(TimeSpan.FromSeconds(2));
        for (int i = 0; i < 30; i++)
        {
            ValueHash256 hash = new Hash256((i + 100).ToString("X64")).ValueHash256;
            Assert.That(credits.Reserve(hashes: [hash]), Is.Not.Null);
            Complete(hash);
        }
        Assert.That(credits.Reserve(hashes: [TestItem.KeccakC.ValueHash256]), Is.Not.Null);
        Complete(oldest);
        ValueHash256 current = TestItem.KeccakD.ValueHash256;
        RetryRequestToken<ValueHash256> token = new(current, 3, 1, long.MaxValue);
        Assert.That(credits.Reserve(hashes: [current], tokens: [token]), Is.Not.Null);
        clock.Advance(TimeSpan.FromSeconds(atMaximumExpiry ? 8 : 6.5));
        Assert.That(credits.TryClaim([new Transaction { Hash = current.ToHash256() }], out _,
            out PooledTransactionRequests.ResponseContext? response), Is.True);
        using (response) Assert.That(response!.Tokens[0].Generation, Is.EqualTo(atMaximumExpiry ? 3 : 0));

        void Complete(ValueHash256 hash)
        {
            Assert.That(credits.TryClaim([new Transaction { Hash = hash.ToHash256() }], out _,
                out PooledTransactionRequests.ResponseContext? response), Is.True);
            response!.Dispose();
        }
    }

    [Test]
    public void Completed_legacy_evidence_does_not_limit_normal_send_throughput()
    {
        ManualTimeProvider clock = new();
        PeerWorkQuota quota = new(8192);
        using LegacyResponseCredits credits = new(clock);
        for (int i = 0; i < 96; i++)
        {
            Assert.That(quota.TryReserve(TestItem.PublicKeyA, PeerWorkQuota.WorkKind.PooledReply, null,
                out PeerWorkQuota.Reservation? reservation), Is.True);
            using (reservation)
                Assert.That(credits.Reserve(reservation, [TestItem.KeccakA.ValueHash256]), Is.Not.Null);
            Assert.That(credits.TryClaim([], out PeerWorkQuota.Reservation? consumed,
                out PooledTransactionRequests.ResponseContext? response), Is.True);
            consumed!.Dispose();
            response!.Dispose();
        }
    }

    [Test]
    public void Expired_legacy_credit_cancellation_does_not_remove_a_new_request()
    {
        long now = 0;
        TimeProvider clock = Substitute.For<TimeProvider>();
        clock.TimestampFrequency.Returns(1000);
        clock.GetTimestamp().Returns(_ => now);
        using LegacyResponseCredits credits = new(clock);
        LinkedListNode<long> expired = credits.Reserve()!;
        now = 60_000;
        Assert.That(credits.Reserve(), Is.Not.Null);
        credits.Cancel(expired);
        Assert.That(credits.Consume(), Is.True);
        Assert.That(credits.Consume(), Is.False);
    }

    [Test]
    public void Legacy_response_credits_are_bounded_and_recover_after_failed_send()
    {
        using LegacyResponseCredits credits = new();
        LinkedListNode<long> first = credits.Reserve()!;
        for (int i = 1; i < 32; i++) Assert.That(credits.Reserve(), Is.Not.Null);
        Assert.That(credits.Reserve(), Is.Null);
        credits.Cancel(first);
        credits.Cancel(first);
        Assert.That(credits.Reserve(), Is.Not.Null);
        for (int i = 0; i < 32; i++) Assert.That(credits.Consume(), Is.True);
        Assert.That(credits.Consume(), Is.False);
    }

    public enum CreditRelease { Cancel, Expire, Consume, Dispose }

    [Test]
    public void Legacy_credits_own_reply_capacity_until_their_release([Values] CreditRelease release)
    {
        ManualTimeProvider clock = new();
        PeerWorkQuota quota = new(8192);
        using LegacyResponseCredits credits = new(clock);
        LinkedListNode<long>? first = null;
        for (int i = 0; i < 32; i++)
        {
            Assert.That(quota.TryReserve(TestItem.PublicKeyA, PeerWorkQuota.WorkKind.PooledReply, null,
                out PeerWorkQuota.Reservation? reservation), Is.True);
            using (reservation)
            {
                LinkedListNode<long>? credit = credits.Reserve(reservation);
                Assert.That(credit, Is.Not.Null);
                first ??= credit;
            }
        }
        Assert.That(quota.TryReserve(TestItem.PublicKeyA, PeerWorkQuota.WorkKind.PooledReply, null, out _), Is.False);
        switch (release)
        {
            case CreditRelease.Cancel:
                credits.Cancel(first!);
                credits.Cancel(first!);
                break;
            case CreditRelease.Expire:
                clock.Advance(Timeouts.Eth - TimeSpan.FromTicks(1));
                Assert.That(quota.TryReserve(TestItem.PublicKeyA, PeerWorkQuota.WorkKind.PooledReply, null, out _), Is.False);
                clock.AdvanceAndFireTimer(TimeSpan.FromTicks(1));
                break;
            case CreditRelease.Consume:
                Assert.That(credits.TryConsume(out PeerWorkQuota.Reservation? claimed), Is.True);
                using (claimed)
                {
                    Assert.That(quota.TryReserve(TestItem.PublicKeyA, PeerWorkQuota.WorkKind.PooledReply, null, out _), Is.False,
                        "a claimed response still owns the slot until its processing owner disposes it");
                }
                break;
            default:
                credits.Dispose();
                break;
        }
        List<PeerWorkQuota.Reservation> recovered = [];
        try
        {
            int available = release is CreditRelease.Expire or CreditRelease.Dispose ? 32 : 1;
            for (int i = 0; i < available; i++)
            {
                Assert.That(quota.TryReserve(TestItem.PublicKeyA, PeerWorkQuota.WorkKind.PooledReply, null,
                    out PeerWorkQuota.Reservation? reservation), Is.True);
                recovered.Add(reservation!);
            }
            Assert.That(quota.TryReserve(TestItem.PublicKeyA, PeerWorkQuota.WorkKind.PooledReply, null, out _), Is.False);
        }
        finally
        {
            foreach (PeerWorkQuota.Reservation reservation in recovered) reservation.Dispose();
        }
    }

    [Test]
    public async Task Local_quota_refusals_remain_neutral_and_leave_another_peer_runnable(
        [Values(0, 1, 2, 3)] int kind, [Values(0, 1, 2)] int privilege, [Values] bool completes)
    {
        HoldingScheduler scheduler = new() { QueueCapacity = 16 };
        using HandlerHarness source = new(scheduler, TestItem.PublicKeyA, "eth");
        source.Session.Node.IsStatic = privilege == 1;
        source.Session.Node.IsTrusted = privilege == 2;
        using HandlerHarness honest = new(scheduler, TestItem.PublicKeyB, "eth");
        long droppedBefore = Metrics.PeerBackgroundRequestsDropped;
        try
        {
            Assert.That(source.Schedule(kind, new OwnedRequest()), Is.True);
            {
                OwnedRequest rejected = new();
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(source.Schedule(kind, rejected), Is.False);
                    Assert.That(rejected.Buffer.ReferenceCount, Is.Zero);
                }
            }
            source.Session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());

            if (completes)
            {
                await scheduler.RunLast();
                Assert.That(source.Schedule(kind, new OwnedRequest()), Is.True);
            }

            OwnedRequest excess = new();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(source.Schedule(kind, excess), Is.False);
                Assert.That(excess.Buffer.ReferenceCount, Is.Zero);
                Assert.That(Metrics.PeerBackgroundRequestsDropped - droppedBefore, Is.EqualTo(2));
            }
            source.Session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());

            OwnedRequest accepted = new();
            Assert.That(honest.Schedule(kind, accepted), Is.True);
            await scheduler.RunLast();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(accepted.Executed, Is.True);
                Assert.That(accepted.Buffer.ReferenceCount, Is.Zero);
            }
        }
        finally
        {
            await scheduler.Drain();
        }
    }
}
