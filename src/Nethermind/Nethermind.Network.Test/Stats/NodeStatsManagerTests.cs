// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Crypto;
using Nethermind.Core.Timers;
using Nethermind.Logging;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

using System.Buffers.Binary;
using Nethermind.Core.Test.Threading;

namespace Nethermind.Network.Test.Stats
{
    public class NodeStatsManagerTests
    {
        [Test]
        public void Published_constructor_signature_is_preserved()
            => Assert.That(typeof(NodeStatsManager).GetConstructor([typeof(ITimerFactory), typeof(ILogManager), typeof(int)]), Is.Not.Null);

        [Test]
        public void Legacy_plugin_manager_inherits_the_nullable_lookup_contract()
        {
            using NodeStatsManager concrete = new(TimerFactory.Default, LimboLogs.Instance);
            INodeStatsManager plugin = new LegacyManager(concrete);
            Node node = new(TestItem.PublicKeyA, "8.8.8.8", 30303);
            Assert.That(plugin.GetStatsOrNull(node), Is.SameAs(concrete.GetOrAdd(node)));
        }

        internal class LegacyManager(INodeStatsManager inner) : INodeStatsManager
        {
            protected INodeStatsManager Inner => inner;
            public INodeStats GetOrAdd(Node node) => inner.GetOrAdd(node);
            public void ReportHandshakeEvent(Node node, ConnectionDirection direction) => inner.ReportHandshakeEvent(node, direction);
            public void ReportEvent(Node node, NodeStatsEventType eventType) => inner.ReportEvent(node, eventType);
            public (bool Result, NodeStatsEventType? DelayReason) IsConnectionDelayed(Node node) => inner.IsConnectionDelayed(node);
            public CompatibilityValidationType? FindCompatibilityValidationResult(Node node) => inner.FindCompatibilityValidationResult(node);
            public long GetCurrentReputation(Node node) => inner.GetCurrentReputation(node);
            public void ReportP2PInitializationEvent(Node node, P2PNodeDetails details) => inner.ReportP2PInitializationEvent(node, details);
            public void ReportSyncPeerInitializeEvent(string protocol, Node node, SyncPeerNodeDetails details) => inner.ReportSyncPeerInitializeEvent(protocol, node, details);
            public void ReportFailedValidation(Node node, CompatibilityValidationType type) => inner.ReportFailedValidation(node, type);
            public void ReportDisconnect(Node node, DisconnectType type, DisconnectReason reason) => inner.ReportDisconnect(node, type, reason);
            public bool IsInboundBanned(Node node, DateTime nowUtc) => inner.IsInboundBanned(node, nowUtc);
            public long GetNewPersistedReputation(Node node) => inner.GetNewPersistedReputation(node);
            public void ReportSyncEvent(Node node, NodeStatsEventType eventType) => inner.ReportSyncEvent(node, eventType);
            public bool HasFailedValidation(Node node) => inner.HasFailedValidation(node);
            public void ReportTransferSpeedEvent(Node node, TransferSpeedType type, long value) => inner.ReportTransferSpeedEvent(node, type, value);
        }

        internal sealed class ForwardingManager(INodeStatsManager inner) : LegacyManager(inner), INodeStatsManager
        {
            public INodeStats? GetStatsOrNull(Node node) => Inner.GetStatsOrNull(node);
            public bool TryAcquire(Node node, bool priority, [NotNullWhen(true)] out INodeStats? stats, out IDisposable? lease)
                => Inner.TryAcquire(node, priority, out stats, out lease);
            public void PromoteLease(IDisposable? lease) => Inner.PromoteLease(lease);
        }

        [Test]
        public void Insertion_should_bound_stats_without_waiting_for_cleanup([Range(0, 20)] int inserted)
        {
            using NodeStatsManager manager = new(TimerFactory.Default, LimboLogs.Instance, 3);
            List<(Node Node, INodeStats Stats)> originals = [];
            for (int i = 0; i < inserted; i++)
            {
                Node node = CreateNode(i);
                originals.Add((node, manager.GetOrAdd(node)));
            }
            List<IDisposable> leases = [];
            try
            {
                for (int i = originals.Count - 1; i >= 0; i--)
                {
                    bool retained = manager.TryAcquire(originals[i].Node, true, out INodeStats? stats, out IDisposable? lease);
                    Assert.That(retained, Is.EqualTo(i >= inserted - 3));
                    if (retained)
                    {
                        leases.Add(lease!);
                        Assert.That(stats, Is.SameAs(originals[i].Stats));
                    }
                }
                Assert.That(leases.Count, Is.EqualTo(Math.Min(inserted, 3)));
            }
            finally
            {
                foreach (IDisposable lease in leases) lease.Dispose();
            }
            Assert.That(manager.TryAcquire(CreateNode(100), true, out _, out IDisposable? recovered), Is.True);
            recovered!.Dispose();
        }

        [Test]
        public void Temporary_priority_does_not_convert_an_ordinary_pin_to_reserved_capacity()
        {
            using NodeStatsManager manager = new(Substitute.For<ITimerFactory>(), LimboLogs.Instance, 4);
            Node[] nodes = TestItem.PublicKeys.Take(4).Select(key => new Node(key, "8.8.8.8", 30303)).ToArray();
            List<IDisposable> leases = [];
            try
            {
                foreach (Node node in nodes.Take(3))
                {
                    Assert.That(manager.TryAcquire(node, false, out _, out IDisposable? ordinary), Is.True);
                    leases.Add(ordinary!);
                    Assert.That(manager.TryAcquire(node, true, out _, out IDisposable? temporary), Is.True);
                    temporary!.Dispose();
                }
                IDisposable? priority;
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(manager.TryAcquire(nodes[3], false, out _, out _), Is.False);
                    Assert.That(manager.TryAcquire(nodes[3], true, out _, out priority), Is.True);
                }
                priority!.Dispose();
                manager.PromoteLease(leases[0]);
                Assert.That(manager.TryAcquire(nodes[3], false, out _, out IDisposable? promoted), Is.True);
                leases.Add(promoted!);
            }
            finally
            {
                foreach (IDisposable lease in leases) lease.Dispose();
            }
        }

        [Test]
        public void Reserved_pins_share_the_bound_and_recover_after_release()
        {
            using NodeStatsManager manager = new(Substitute.For<ITimerFactory>(), LimboLogs.Instance, 4);
            Node[] nodes = TestItem.PublicKeys.Take(5).Select(key => new Node(key, IPAddress.Loopback.ToString(), 30303)).ToArray();
            List<IDisposable> leases = [];
            try
            {
                for (int i = 0; i < 3; i++)
                {
                    Assert.That(manager.TryAcquire(nodes[i], false, out _, out IDisposable? lease), Is.True);
                    leases.Add(lease!);
                }
                IDisposable? priority;
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(manager.TryAcquire(nodes[3], false, out _, out _), Is.False);
                    Assert.That(manager.TryAcquire(nodes[3], true, out _, out priority), Is.True);
                }
                leases.Add(priority!);
                Assert.That(manager.TryAcquire(nodes[4], true, out _, out _), Is.False);
                Assert.Throws<NodeStatsCapacityException>(() => manager.GetOrAdd(nodes[4]));


                leases[0].Dispose();
                Assert.That(manager.TryAcquire(nodes[4], false, out _, out IDisposable? recovered), Is.True);
                leases.Add(recovered!);

            }
            finally
            {
                foreach (IDisposable lease in leases) lease.Dispose();
            }
        }

        [Test]
        public void Pin_growth_reclaims_protection_without_extending_its_original_deadline([Values] bool ban)
        {
            ManualTimeProvider clock = new();
            using NodeStatsManager stats = new(TimerFactory.Default, LimboLogs.Instance, 4, clock);
            Node rejected = CreateNode(1);
            Protect(stats, rejected, ban);
            INodeStats original = stats.GetOrAdd(rejected);
            clock.Advance(TimeSpan.FromMinutes(2));
            List<IDisposable> leases = [];
            try
            {
                for (int i = 2; i <= 4; i++)
                {
                    Assert.That(stats.TryAcquire(CreateNode(i), true, out _, out IDisposable? lease), Is.True);
                    leases.Add(lease!);
                }
                Assert.That(stats.TryGetOrAdd(CreateNode(5), out _), Is.True);
                INodeStats recreated = stats.GetOrAdd(rejected);
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(recreated, Is.Not.SameAs(original));
                    Assert.That(stats.ProtectedCount, Is.Zero);
                    Assert.That(stats.IsInboundBanned(rejected, clock.GetUtcNow().UtcDateTime), Is.EqualTo(ban));
                    Assert.That(recreated.IsConnectionDelayed(clock.GetUtcNow().UtcDateTime).Result, Is.EqualTo(ban));
                }
                clock.Advance(TimeSpan.FromMinutes(13) - TimeSpan.FromTicks(1));
                Assert.That(stats.IsInboundBanned(rejected, clock.GetUtcNow().UtcDateTime), Is.EqualTo(ban));
                clock.Advance(TimeSpan.FromTicks(1));
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(stats.IsInboundBanned(rejected, clock.GetUtcNow().UtcDateTime), Is.False);
                    Assert.That(recreated.IsInboundBanned(clock.GetUtcNow().UtcDateTime), Is.False);
                }
            }
            finally { foreach (IDisposable lease in leases) lease.Dispose(); }
        }

        [Test]
        public void Unpin_does_not_delay_an_earlier_protection_deadline([Values] bool ban)
        {
            ManualTimeProvider clock = new();
            using NodeStatsManager stats = new(TimerFactory.Default, LimboLogs.Instance, 6, clock);
            Node early = CreateNode(1);
            Protect(stats, early, ban);
            Assert.That(stats.TryAcquire(early, true, out _, out IDisposable? lease), Is.True);
            clock.Advance(TimeSpan.FromMinutes(2));
            Protect(stats, CreateNode(2), ban);
            lease!.Dispose();
            Assert.That(stats.ProtectedCount, Is.EqualTo(2));

            clock.Advance(TimeSpan.FromMinutes(13));
            stats.GetOrAdd(CreateNode(3));
            Assert.That(stats.ProtectedCount, Is.EqualTo(1));
            clock.Advance(TimeSpan.FromMinutes(2));
            stats.GetOrAdd(CreateNode(4));
            Assert.That(stats.ProtectedCount, Is.Zero);
        }

        [Test]
        public void Pin_growth_demotes_compatibility_records_without_consuming_all_free_space()
        {
            using NodeStatsManager stats = new(TimerFactory.Default, LimboLogs.Instance, 8);
            for (int i = 1; i <= 4; i++) Protect(stats, CreateNode(i), false);
            for (int i = 5; i <= 8; i++) stats.GetOrAdd(CreateNode(i));
            List<IDisposable> leases = [];
            try
            {
                for (int i = 5; i <= 8; i++)
                {
                    Assert.That(stats.TryAcquire(CreateNode(i), true, out _, out IDisposable? lease), Is.True);
                    leases.Add(lease!);
                }
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(stats.ProtectedCount, Is.EqualTo(2));
                    Assert.That(stats.TryGetOrAdd(CreateNode(9), out _), Is.True);
                }

            }
            finally
            {
                foreach (IDisposable lease in leases) lease.Dispose();
            }
        }

        [Test]
        public void Pin_growth_preserves_ban_refusal_after_reclaiming_the_oldest_stats([Values] bool banPinned)
        {
            ManualTimeProvider clock = new();
            using NodeStatsManager stats = new(TimerFactory.Default, LimboLogs.Instance, 8, clock);
            INodeStats[] originals = new INodeStats[4];
            for (int i = 1; i <= 4; i++)
            {
                Protect(stats, CreateNode(i), true);
                originals[i - 1] = stats.GetOrAdd(CreateNode(i));
            }
            List<IDisposable> leases = [];
            try
            {
                for (int i = 5; i <= 8; i++)
                {
                    Assert.That(stats.TryAcquire(CreateNode(i), true, out _, out IDisposable? lease), Is.True);
                    leases.Add(lease!);
                }
                if (banPinned) Protect(stats, CreateNode(5), true);
                Assert.That(stats.ProtectedCount, Is.EqualTo(2));
                for (int i = 9; i <= 16; i++) Assert.That(stats.TryGetOrAdd(CreateNode(i), out _), Is.True);
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(stats.GetOrAdd(CreateNode(1)), Is.Not.SameAs(originals[0]));
                    for (int i = 1; i <= 4; i++)
                        Assert.That(stats.IsInboundBanned(CreateNode(i), clock.GetUtcNow().UtcDateTime), Is.True);
                }
                clock.Advance(TimeSpan.FromMinutes(15));
                using (Assert.EnterMultipleScope())
                {
                    for (int i = 1; i <= 4; i++) Assert.That(stats.IsInboundBanned(CreateNode(i), clock.GetUtcNow().UtcDateTime), Is.False);
                }
            }
            finally { foreach (IDisposable lease in leases) lease.Dispose(); }
        }

        [Test]
        public void A_ban_recorded_after_pinned_expiry_applies_new_ban_pressure()
        {
            ManualTimeProvider clock = new();
            using NodeStatsManager stats = new(TimerFactory.Default, LimboLogs.Instance, 8, clock);
            Protect(stats, CreateNode(1), true);
            Assert.That(stats.TryAcquire(CreateNode(1), true, out _, out IDisposable? expiredBan), Is.True);
            using IDisposable held = expiredBan!;
            clock.Advance(TimeSpan.FromMinutes(15));
            Assert.That(stats.ProtectedCount, Is.Zero);
            for (int i = 2; i <= 4; i++) Protect(stats, CreateNode(i), true);
            List<IDisposable> leases = [];
            try
            {
                for (int i = 5; i <= 8; i++)
                {
                    Assert.That(stats.TryAcquire(CreateNode(i), true, out _, out IDisposable? lease), Is.True);
                    leases.Add(lease!);
                }
                Assert.That(stats.ProtectedCount, Is.EqualTo(1));
                Protect(stats, CreateNode(1), true);
                held.Dispose();
                Assert.That(stats.ProtectedCount, Is.EqualTo(2));
            }
            finally
            {
                foreach (IDisposable lease in leases) lease.Dispose();
            }
        }

        [Test]
        public void Releasing_a_suppressed_ban_does_not_demote_other_live_bans()
        {
            using NodeStatsManager stats = new(TimerFactory.Default, LimboLogs.Instance, 10);
            for (int i = 1; i <= 6; i++) Protect(stats, CreateNode(i), true);
            List<IDisposable> leases = [];
            try
            {
                for (int i = 7; i <= 10; i++)
                {
                    Assert.That(stats.TryAcquire(CreateNode(i), true, out _, out IDisposable? lease), Is.True);
                    leases.Add(lease!);
                }
                IDisposable? suppressed;
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(stats.ProtectedCount, Is.EqualTo(5));
                    Assert.That(stats.TryAcquire(CreateNode(1), true, out _, out suppressed), Is.True);
                    Assert.That(stats.ProtectedCount, Is.EqualTo(2));
                }
                suppressed!.Dispose();
                Assert.That(stats.ProtectedCount, Is.EqualTo(2));
            }
            finally
            {
                foreach (IDisposable lease in leases) lease.Dispose();
            }
        }

        [Test]
        public void A_new_unpinned_ban_demotes_only_the_oldest_protection_at_the_target()
        {
            ManualTimeProvider clock = new();
            using NodeStatsManager stats = new(TimerFactory.Default, LimboLogs.Instance, 8, clock);
            Protect(stats, CreateNode(1), true);
            Protect(stats, CreateNode(2), true);
            INodeStats first = stats.GetOrAdd(CreateNode(1));
            INodeStats second = stats.GetOrAdd(CreateNode(2));
            List<IDisposable> leases = [];
            try
            {
                for (int i = 5; i <= 8; i++)
                {
                    Assert.That(stats.TryAcquire(CreateNode(i), true, out _, out IDisposable? lease), Is.True);
                    leases.Add(lease!);
                }
                Assert.That(stats.ProtectedCount, Is.EqualTo(2));
                Protect(stats, CreateNode(3), true);
                INodeStats third = stats.GetOrAdd(CreateNode(3));
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(stats.ProtectedCount, Is.EqualTo(2));
                    Assert.That(stats.TryGetOrAdd(CreateNode(9), out _), Is.True);
                    Assert.That(stats.TryGetOrAdd(CreateNode(10), out _), Is.True);
                    Assert.That(stats.TryGetOrAdd(CreateNode(11), out _), Is.True);
                    Assert.That(stats.GetOrAdd(CreateNode(1)), Is.Not.SameAs(first));
                    Assert.That(stats.GetOrAdd(CreateNode(2)), Is.SameAs(second));
                    Assert.That(stats.GetOrAdd(CreateNode(3)), Is.SameAs(third));
                }
                using (Assert.EnterMultipleScope())
                {
                    for (int i = 1; i <= 3; i++)
                        Assert.That(stats.IsInboundBanned(CreateNode(i), clock.GetUtcNow().UtcDateTime), Is.True);
                }
            }
            finally
            {
                foreach (IDisposable lease in leases) lease.Dispose();
            }
        }

        [Test]
        public void Compatibility_spam_cannot_demote_a_live_ban([Values] bool floodBeforeBan)
        {
            ManualTimeProvider clock = new();
            using NodeStatsManager stats = new(TimerFactory.Default, LimboLogs.Instance, 4, clock);
            Node banned = CreateNode(1);
            INodeStats? retained = null;
            if (!floodBeforeBan) RecordBan();
            for (int i = 2; i < 10002; i++)
                stats.ReportFailedValidation(CreateNode(i), CompatibilityValidationType.Capabilities);
            using (Assert.EnterMultipleScope())
            {

                Assert.That(stats.ProtectedCount, Is.EqualTo(2));
            }
            if (floodBeforeBan) RecordBan();

            AssertRetainedBan();
            Assert.That(stats.TryGetOrAdd(CreateNode(10002), out _), Is.True);
            for (int i = 10003; i < 20003; i++)
                stats.ReportEvent(CreateNode(i), NodeStatsEventType.DiscoveryPongIn);

            AssertRetainedBan();

            void AssertRetainedBan()
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(stats.GetOrAdd(banned), Is.SameAs(retained));
                    Assert.That(retained!.IsInboundBanned(clock.GetUtcNow().UtcDateTime), Is.True);

                    Assert.That(stats.ProtectedCount, Is.EqualTo(2));
                }
            }

            void RecordBan()
            {
                stats.ReportDisconnect(banned, DisconnectType.Local, DisconnectReason.BreachOfProtocol);
                retained = stats.GetOrAdd(banned);
            }
        }

        [Test]
        public void Repeated_protection_reindexes_the_deadline_without_hiding_other_expirations([Values] bool ban)
        {
            ManualTimeProvider clock = new();
            using NodeStatsManager stats = new(TimerFactory.Default, LimboLogs.Instance, 6, clock);
            Node extended = CreateNode(1);
            Protect(stats, extended, ban);
            Protect(stats, CreateNode(2), ban);
            Protect(stats, CreateNode(3), ban);
            clock.Advance(TimeSpan.FromMinutes(2));
            Protect(stats, extended, ban);
            clock.Advance(TimeSpan.FromMinutes(13));

            stats.GetOrAdd(CreateNode(4));
            Assert.That(stats.ProtectedCount, Is.EqualTo(1));
            clock.Advance(TimeSpan.FromMinutes(2));
            stats.GetOrAdd(CreateNode(5));
            Assert.That(stats.ProtectedCount, Is.Zero);
        }

        [Test]
        public void New_ban_refusal_survives_admission_and_discovery_when_protection_is_reclaimed([Values] bool olderBans)
        {
            ManualTimeProvider clock = new();
            using NodeStatsManager stats = new(TimerFactory.Default, LimboLogs.Instance, 4, clock);
            Protect(stats, CreateNode(1), olderBans);
            Protect(stats, CreateNode(2), olderBans);
            IDisposable? third;
            IDisposable? fourth;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(stats.TryAcquire(CreateNode(3), true, out _, out third), Is.True);
                Assert.That(stats.TryAcquire(CreateNode(4), true, out _, out fourth), Is.True);
            }
            using IDisposable? thirdLease = third;
            using IDisposable? fourthLease = fourth;
            IDisposable? compatibilityLease = null;
            if (!olderBans)
                Assert.That(stats.TryAcquire(CreateNode(1), true, out _, out compatibilityLease), Is.True);
            using IDisposable? pinnedCompatibility = compatibilityLease;
            Assert.That(stats.TryGetOrAdd(CreateNode(5), out _), Is.True);
            Node banned = CreateNode(5);

            stats.ReportDisconnect(banned, DisconnectType.Local, DisconnectReason.BreachOfProtocol);

            INodeStats retained = stats.GetOrAdd(banned);
            clock.Advance(TimeSpan.FromMinutes(2));
            for (int i = 6; i < 106; i++) stats.ReportEvent(CreateNode(i), NodeStatsEventType.DiscoveryPongIn);
            INodeStats current = stats.GetOrAdd(banned);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(current, olderBans ? Is.SameAs(retained) : Is.Not.SameAs(retained));
                Assert.That(stats.IsInboundBanned(banned, clock.GetUtcNow().UtcDateTime), Is.True);
                Assert.That(current.IsInboundBanned(clock.GetUtcNow().UtcDateTime), Is.True);
            }
            clock.Advance(TimeSpan.FromMinutes(13) - TimeSpan.FromTicks(1));
            Assert.That(stats.IsInboundBanned(banned, clock.GetUtcNow().UtcDateTime), Is.True);
            clock.Advance(TimeSpan.FromTicks(1));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(stats.IsInboundBanned(banned, clock.GetUtcNow().UtcDateTime), Is.False);
                Assert.That(current.IsInboundBanned(clock.GetUtcNow().UtcDateTime), Is.False);
            }
        }

        private static void Protect(NodeStatsManager stats, Node node, bool ban)
        {
            if (ban) stats.ReportDisconnect(node, DisconnectType.Local, DisconnectReason.BreachOfProtocol);
            else stats.ReportFailedValidation(node, CompatibilityValidationType.NetworkId);
        }

        [Test]
        public void Protected_last_slot_admits_fresh_stats_without_erasing_ban_evidence()
        {
            ManualTimeProvider clock = new();
            using NodeStatsManager stats = new(TimerFactory.Default, LimboLogs.Instance, 1, clock);
            Node banned = CreateNode(1);
            Protect(stats, banned, true);
            INodeStats original = stats.GetOrAdd(banned);
            Assert.That(stats.TryGetOrAdd(CreateNode(2), out _), Is.True);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(stats.GetOrAdd(banned), Is.Not.SameAs(original));
                Assert.That(stats.GetOrAdd(banned).IsInboundBanned(clock.GetUtcNow().UtcDateTime), Is.True);
                Assert.That(stats.IsInboundBanned(banned, clock.GetUtcNow().UtcDateTime), Is.True);
            }
        }

        [Test]
        public void Default_capacity_pin_growth_reserves_exact_admission_floor()
        {
            ManualTimeProvider clock = new();
            using NodeStatsManager stats = new(TimerFactory.Default, LimboLogs.Instance, 10000, clock);
            for (int i = 0; i < 1024; i++) Protect(stats, CreateNode(i), true);
            List<IDisposable> leases = [];
            try
            {
                for (int i = 0; i < 8976; i++)
                {
                    Assert.That(stats.TryAcquire(CreateNode(100000 + i), false, out _, out IDisposable? lease), Is.True);
                    leases.Add(lease!);
                }
                Assert.That(stats.ProtectedCount, Is.EqualTo(399));
                Node fresh = CreateNode(200000);
                Assert.That(stats.TryGetOrAdd(fresh, out _), Is.True);
                Protect(stats, fresh, true);
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(stats.IsInboundBanned(fresh, clock.GetUtcNow().UtcDateTime), Is.True);
                    for (int i = 0; i < 1024; i++)
                        Assert.That(stats.IsInboundBanned(CreateNode(i), clock.GetUtcNow().UtcDateTime), Is.True);
                    Assert.That(stats.ProtectedCount, Is.EqualTo(399));
                }
            }
            finally { foreach (IDisposable lease in leases) lease.Dispose(); }
        }

        [Test]
        public void Five_hundred_thirteen_ban_sessions_retain_the_first_refusal_after_stats_eviction()
        {
            ManualTimeProvider clock = new();
            using FilledCandidates context = new(clock);
            Node first = CreateNode(0);
            Protect(context.Stats, first, true);
            INodeStats original = context.Stats.GetOrAdd(first);
            for (int i = 1; i < 513; i++) Protect(context.Stats, CreateNode(i), true);
            for (int i = 1000; i < 3000; i++) context.Stats.ReportEvent(CreateNode(i), NodeStatsEventType.DiscoveryPongIn);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(context.Stats.GetOrAdd(first), Is.Not.SameAs(original));
                Assert.That(context.Stats.GetOrAdd(first).IsInboundBanned(clock.GetUtcNow().UtcDateTime), Is.True);
                Assert.That(context.Stats.ProtectedCount, Is.EqualTo(399));
            }
        }

        [Test]
        public void Non_ban_disconnect_does_not_erase_or_extend_attributable_evidence([Values] bool remote)
        {
            ManualTimeProvider clock = new();
            using NodeStatsManager stats = new(TimerFactory.Default, LimboLogs.Instance, 4, clock);
            Node rejected = CreateNode(1);
            Node honest = CreateNode(2);
            Protect(stats, rejected, true);
            clock.Advance(TimeSpan.FromMinutes(2));
            stats.ReportDisconnect(rejected, remote ? DisconnectType.Remote : DisconnectType.Local, DisconnectReason.UselessPeer);
            stats.ReportDisconnect(honest, remote ? DisconnectType.Remote : DisconnectType.Local, DisconnectReason.UselessPeer);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(stats.GetOrAdd(rejected).IsInboundBanned(clock.GetUtcNow().UtcDateTime), Is.True);
                Assert.That(stats.IsInboundBanned(honest, clock.GetUtcNow().UtcDateTime), Is.False);
            }
            clock.Advance(TimeSpan.FromMinutes(13));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(stats.GetOrAdd(rejected).IsInboundBanned(clock.GetUtcNow().UtcDateTime), Is.False);
                Assert.That(stats.IsInboundBanned(rejected, clock.GetUtcNow().UtcDateTime), Is.False);
            }
        }

        [Test, NonParallelizable]
        public void Zero_duration_ban_policy_does_not_erase_captured_deadline_or_ban_honest_identity()
        {
            ManualTimeProvider clock = new();
            using NodeStatsManager stats = new(TimerFactory.Default, LimboLogs.Instance, 4, clock);
            Node banned = CreateNode(1);
            Node honest = CreateNode(2);
            Protect(stats, banned, true);
            (TimeSpan ReconnectDelay, long ReputationScore) original = StatsParameters.Instance.LocalDisconnectParams[DisconnectReason.BreachOfProtocol];
            try
            {
                StatsParameters.Instance.LocalDisconnectParams[DisconnectReason.BreachOfProtocol] = (TimeSpan.Zero, original.ReputationScore);
                clock.Advance(TimeSpan.FromMinutes(2));
                Protect(stats, banned, true);
                Protect(stats, honest, true);
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(stats.IsInboundBanned(banned, clock.GetUtcNow().UtcDateTime), Is.True);
                    Assert.That(stats.GetOrAdd(banned).IsInboundBanned(clock.GetUtcNow().UtcDateTime), Is.True);
                    Assert.That(stats.IsInboundBanned(honest, clock.GetUtcNow().UtcDateTime), Is.False);
                }
                clock.Advance(TimeSpan.FromMinutes(13));
                Assert.That(stats.IsInboundBanned(banned, clock.GetUtcNow().UtcDateTime), Is.False);
            }
            finally { StatsParameters.Instance.LocalDisconnectParams[DisconnectReason.BreachOfProtocol] = original; }
        }

        [Test]
        public void accepted_residual_three_unique_bans_overwrite_capacity_one_history_after_stats_eviction()
        {
            // At capacity one, ban two demotes the first; ban three overwrites its one-entry history.
            ManualTimeProvider clock = new();
            using NodeStatsManager stats = new(TimerFactory.Default, LimboLogs.Instance, 1, clock);
            Node first = CreateNode(1);
            Protect(stats, first, true);
            Protect(stats, CreateNode(2), true);
            Assert.That(stats.IsInboundBanned(first, clock.GetUtcNow().UtcDateTime), Is.True);
            Protect(stats, CreateNode(3), true);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(stats.IsInboundBanned(first, clock.GetUtcNow().UtcDateTime), Is.False);
                Assert.That(stats.IsInboundBanned(CreateNode(2), clock.GetUtcNow().UtcDateTime), Is.True);
                Assert.That(stats.IsInboundBanned(CreateNode(3), clock.GetUtcNow().UtcDateTime), Is.True);
            }
        }

        [Test]
        public void accepted_residual_discovery_does_not_reinsert_history_overflowed_by_seven_unique_bans()
        {
            ManualTimeProvider clock = new();
            using NodeStatsManager stats = new(TimerFactory.Default, LimboLogs.Instance, 3, clock);
            Node suppressed = CreateNode(1);
            Protect(stats, suppressed, true);
            Protect(stats, CreateNode(2), true);
            Assert.That(stats.TryAcquire(suppressed, true, out INodeStats? original, out IDisposable? lease), Is.True);
            using (lease)
            {
                // Seven distinct attributable bans overflow three history slots; the pin still owns the first stats entry.
                for (int i = 3; i <= 7; i++) Protect(stats, CreateNode(i), true);
                Assert.That(stats.IsInboundBanned(suppressed, clock.GetUtcNow().UtcDateTime), Is.True);
            }
            for (int i = 100; i < 110; i++) stats.ReportEvent(CreateNode(i), NodeStatsEventType.DiscoveryPongIn);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(stats.GetOrAdd(suppressed), Is.Not.SameAs(original));
                Assert.That(stats.IsInboundBanned(suppressed, clock.GetUtcNow().UtcDateTime), Is.False);
                Assert.That(stats.IsInboundBanned(CreateNode(4), clock.GetUtcNow().UtcDateTime), Is.True);
            }
        }

        [Test]
        public void Renewing_a_history_identity_does_not_move_its_fifo_position()
        {
            ManualTimeProvider clock = new();
            using NodeStatsManager stats = new(TimerFactory.Default, LimboLogs.Instance, 3, clock);
            for (int i = 1; i <= 5; i++) Protect(stats, CreateNode(i), true);
            clock.Advance(TimeSpan.FromTicks(1));
            Protect(stats, CreateNode(2), true);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(stats.IsInboundBanned(CreateNode(3), clock.GetUtcNow().UtcDateTime), Is.True);
                Assert.That(stats.IsInboundBanned(CreateNode(4), clock.GetUtcNow().UtcDateTime), Is.True);
                Assert.That(stats.IsInboundBanned(CreateNode(5), clock.GetUtcNow().UtcDateTime), Is.True);
            }
        }

        [Test]
        public void Compatibility_cap_admits_new_work_without_displacing_a_live_ban()
        {
            using NodeStatsManager stats = new(TimerFactory.Default, LimboLogs.Instance, 2);
            Node wrongChain = CreateNode(1);
            Node banned = CreateNode(2);
            stats.ReportFailedValidation(wrongChain, CompatibilityValidationType.NetworkId);
            stats.ReportDisconnect(banned, DisconnectType.Local, DisconnectReason.BreachOfProtocol);

            Assert.That(stats.TryGetOrAdd(CreateNode(3), out _), Is.True);
            for (int i = 4; i < 10003; i++) stats.ReportEvent(CreateNode(i), NodeStatsEventType.DiscoveryPongIn);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(stats.TryGetOrAdd(CreateNode(10003), out _), Is.True);
                Assert.That(stats.ProtectedCount, Is.EqualTo(1));
                Assert.That(stats.GetOrAdd(banned).IsInboundBanned(DateTime.UtcNow), Is.True);

            }
        }

        [Test]
        public void Recently_reported_unleased_identity_survives_a_cold_neighbor()
        {
            using NodeStatsManager stats = new(TimerFactory.Default, LimboLogs.Instance, 2);
            Node hot = CreateNode(1);
            Node cold = CreateNode(2);
            stats.ReportEvent(hot, NodeStatsEventType.DiscoveryPingIn);
            stats.ReportEvent(cold, NodeStatsEventType.DiscoveryPongIn);
            stats.ReportEvent(hot, NodeStatsEventType.DiscoveryPongIn);

            stats.ReportEvent(CreateNode(3), NodeStatsEventType.DiscoveryPingIn);

            Assert.That(stats.GetOrAdd(hot).DidEventHappen(NodeStatsEventType.DiscoveryPingIn), Is.True);
        }

        [Test]
        public void Ban_or_wrong_chain_record_survives_ten_thousand_discovery_bonds([Values] RetainedReason reason)
        {
            using FilledCandidates context = new();
            Node rejected = new(TestItem.PublicKeyA, "8.8.4.4", 30303);
            if (reason == RetainedReason.WrongChain) context.Stats.ReportFailedValidation(rejected, CompatibilityValidationType.NetworkId);
            else context.Stats.ReportDisconnect(rejected, reason == RetainedReason.LocalBreach ? DisconnectType.Local : DisconnectType.Remote, DisconnectReason.BreachOfProtocol);
            for (int i = 0; i < 10000; i++)
                context.Stats.ReportEvent(CreateNode(i), NodeStatsEventType.DiscoveryPongIn);

            using (Assert.EnterMultipleScope())
            {
                if (reason == RetainedReason.WrongChain)
                    Assert.That(context.Stats.FindCompatibilityValidationResult(rejected), Is.EqualTo(CompatibilityValidationType.NetworkId));
                else
                    Assert.That(context.Stats.GetOrAdd(rejected).IsInboundBanned(context.Clock.GetUtcNow().UtcDateTime), Is.EqualTo(reason == RetainedReason.LocalBreach));

                Assert.That(context.Stats.TryGetOrAdd(CreateNode(20000), out _), Is.True, "unrelated discovery work still has bounded space");
            }
        }

        [Test]
        public void Full_candidate_pool_keeps_a_thousand_recent_bond_records()
        {
            using FilledCandidates context = new();
            for (int i = 0; i < 1000; i++)
                context.Stats.ReportEvent(CreateNode(i), NodeStatsEventType.DiscoveryPongIn);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(context.Stats.GetOrAdd(CreateNode(0)).DidEventHappen(NodeStatsEventType.DiscoveryPongIn), Is.True);

            }
        }

        public enum RetainedReason { WrongChain, LocalBreach, RemoteClaim }

        private static Node CreateNode(int id)
        {
            byte[] key = new byte[PublicKey.LengthInBytes];
            BinaryPrimitives.WriteInt32LittleEndian(key, id + 1);
            return new Node(new PublicKey(key), "9.9.9.9", 30303);
        }

        private sealed class FilledCandidates : IDisposable
        {
            private readonly List<IDisposable> _leases = [];
            public ManualTimeProvider Clock { get; }
            public NodeStatsManager Stats { get; }

            public FilledCandidates(ManualTimeProvider? clock = null)
            {
                Clock = clock ?? new ManualTimeProvider();
                Stats = new NodeStatsManager(TimerFactory.Default, LimboLogs.Instance, 10000, Clock);
                for (int i = 0; i < 10000; i++)
                {
                    if (!Stats.TryAcquire(CreateNode(i + 100000), false, out _, out IDisposable? lease)) break;
                    _leases.Add(lease!);
                }
                Assert.That(_leases.Count, Is.EqualTo(8976), "exercise a full ordinary candidate allocation");
            }

            public void Dispose()
            {
                foreach (IDisposable lease in _leases) lease.Dispose();
                Stats.Dispose();
            }
        }
    }
}
