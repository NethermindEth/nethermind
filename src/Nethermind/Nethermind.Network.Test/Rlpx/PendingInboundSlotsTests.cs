// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using System.Threading.Tasks;
using DotNetty.Transport.Channels;
using Nethermind.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Analyzers;
using Nethermind.Network.Rlpx;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test.Rlpx;

public class PendingInboundSlotsTests
{
    [TestCase("192.0.2.1", "192.0.2.1", "192.0.2.2")]
    [TestCase("2001:db8::1", "2001:db8::2", "2001:db8:0:1::1")]
    public void Source_limit_and_release(string firstAddress, string sameSourceAddress, string otherAddress)
    {
        PendingInboundSlots slots = new(3, 1);
        PendingInboundSlots.Source first = Source(firstAddress);
        PendingInboundSlots.Source same = Source(sameSourceAddress);
        PendingInboundSlots.Source other = Source(otherAddress);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(slots.TryReserve(first, false, out _), Is.True);
            Assert.That(slots.TryReserve(same, false, out bool sourceLimit), Is.False);
            Assert.That(sourceLimit, Is.True);
            Assert.That(slots.TryReserve(other, false, out _), Is.True);
            Assert.That(slots.Count, Is.EqualTo(2));
            slots.Release(first, false);
            Assert.That(slots.TryReserve(same, false, out _), Is.True);
            slots.Release(same, false);
            slots.Release(other, false);
            Assert.That(slots.Count, Is.Zero);
        }
    }

    [Test]
    public async Task Authentication_and_close_return_the_slot_only_once([Values] bool authenticateFirst)
    {
        PendingInboundSlots slots = new(1, 1);
        PendingInboundSlots.Source source = Source("192.0.2.1");
        TaskCompletionSource close = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IChannel channel = Substitute.For<IChannel>();
        channel.CloseCompletion.Returns(close.Task);
        Session session = new(30303, channel, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
        Assert.That(slots.TryReserve(source, false, out _), Is.True);
        RlpxHost.PendingInboundAdmission admission = new(slots, session, channel, false, source);

        if (authenticateFirst) session.MarkAuthenticated();
        else
        {
            close.SetResult();
            await admission.ChannelClosed;
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(slots.Count, Is.Zero);
            Assert.That(slots.TryReserve(source, false, out _), Is.True);
            if (authenticateFirst)
            {
                close.SetResult();
                await admission.ChannelClosed;
            }
            else session.MarkAuthenticated();
            Assert.That(slots.Count, Is.EqualTo(1));
            slots.Release(source, false);
            Assert.That(slots.Count, Is.Zero);
        }
    }

    [Test]
    public void Full_lane_does_not_consume_a_source_slot()
    {
        PendingInboundSlots slots = new(1, 1);
        PendingInboundSlots.Source first = Source("192.0.2.1");
        PendingInboundSlots.Source other = Source("192.0.2.2");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(slots.TryReserve(first, false, out _), Is.True);
            Assert.That(slots.TryReserve(other, false, out bool sourceLimit), Is.False);
            Assert.That(sourceLimit, Is.False);
            Assert.That(slots.TryReserve(other, true, out _), Is.True);
            slots.Release(first, false);
            Assert.That(slots.TryReserve(other, false, out _), Is.True);
            slots.Release(other, false);
            slots.Release(other, true);
            Assert.That(slots.Count, Is.Zero);
        }
    }

    [Test]
    public void Privileged_lane_has_its_own_limit_and_releases_slots()
    {
        PendingInboundSlots slots = new(1, 1);
        PendingInboundSlots.Source source = Source("192.0.2.1");
        Assert.That(slots.TryReserve(source, false, out _), Is.True);
        for (int i = 0; i < PendingInboundSlots.PrivilegedLimit; i++)
            Assert.That(slots.TryReserve(source, true, out _), Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(slots.TryReserve(source, true, out _), Is.False);
            slots.Release(source, true);
            Assert.That(slots.TryReserve(source, true, out _), Is.True);
        }
    }

    [Test]
    public void Ipv6_prefix_slot_is_available_after_release()
    {
        PendingInboundSlots slots = new(8, 1);
        PendingInboundSlots.Source[] sources = new PendingInboundSlots.Source[5];
        for (int i = 0; i < sources.Length; i++) sources[i] = Source($"2001:db8:0:{i}::1");
        for (int i = 0; i < 4; i++) Assert.That(slots.TryReserve(sources[i], false, out _), Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(slots.TryReserve(sources[4], false, out bool sourceLimit), Is.False);
            Assert.That(sourceLimit, Is.True);
            slots.Release(sources[0], false);
            Assert.That(slots.TryReserve(sources[4], false, out _), Is.True);
        }
    }

    private static PendingInboundSlots.Source Source(string address) => PendingInboundSlots.Source.From(IPAddress.Parse(address));
}
