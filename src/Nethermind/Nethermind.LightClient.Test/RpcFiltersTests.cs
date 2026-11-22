// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.LightClient.Rpc;
using NSubstitute;

namespace Nethermind.LightClient.Test;

[TestFixture]
public class RpcFiltersTests
{
    [Test]
    public async Task Empty_polls_renew_expiry_and_expired_filters_release_capacity()
    {
        ManualClock clock = new();
        RpcFilters filters = new(clock);
        using RpcBackend rpc = Backend();
        UInt256 id = filters.Install();
        for (int i = 1; i < RpcFilters.MaxFilters; i++) filters.Install();
        Assert.That(Assert.Throws<RpcException>(() => filters.Install())!.Code, Is.EqualTo(ErrorCodes.LimitExceeded));

        clock.Advance(RpcFilters.IdleTimeout - TimeSpan.FromTicks(1));
        JsonElement empty = await filters.PollAsync(id, rpc, CancellationToken.None);
        Assert.That(empty.GetArrayLength(), Is.Zero);
        clock.Advance(TimeSpan.FromTicks(1));
        UInt256 replacement = filters.Install();
        Assert.That((await filters.PollAsync(id, rpc, CancellationToken.None)).GetArrayLength(), Is.Zero);

        clock.Advance(RpcFilters.IdleTimeout);
        RpcException? expired = await Assert.ThrowsAsync<RpcException>(async () => await filters.PollAsync(id, rpc, CancellationToken.None));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(expired!.Code, Is.EqualTo(ErrorCodes.InvalidInput));
            Assert.That(filters.Uninstall(replacement), Is.False);
        }
    }

    [Test]
    public async Task Head_history_overflow_is_reported_when_an_active_filter_falls_behind()
    {
        RpcFilters filters = new();
        using RpcBackend rpc = Backend();
        UInt256 slow = filters.Install();
        UInt256 active = filters.Install();
        for (int i = 0; i <= RpcFilters.MaxHeadChanges; i++)
        {
            long sequence = filters.StartPublication();
            Hash256 hash = Keccak.Compute($"head {sequence}");
            filters.RecordHead(new(sequence, [], [hash], []));
            JsonElement changes = await filters.PollAsync(active, rpc, CancellationToken.None);
            Assert.That(changes.EnumerateArray().Select(item => item.GetString()), Is.EqualTo(new[] { hash.ToString() }));
        }

        RpcException? overflowed = await Assert.ThrowsAsync<RpcException>(async () => await filters.PollAsync(slow, rpc, CancellationToken.None));
        JsonElement consumed = await filters.PollAsync(active, rpc, CancellationToken.None);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(overflowed!.Code, Is.EqualTo(ErrorCodes.LimitExceeded));
            Assert.That(filters.Uninstall(slow), Is.False);
            Assert.That(consumed.GetArrayLength(), Is.Zero);
        }
    }

    [Test]
    public async Task Pending_hashes_are_ordered_per_filter_and_overflow_is_explicit()
    {
        RpcFilters filters = new();
        using RpcBackend rpc = Backend();
        UInt256 slow = filters.Install(pending: true);
        Hash256 first = Keccak.Compute("first pending");
        filters.PublishPending(first);
        UInt256 recent = filters.Install(pending: true);
        Hash256 second = Keccak.Compute("second pending");
        filters.PublishPending(second);

        JsonElement changes = await filters.PollAsync(slow, rpc, CancellationToken.None);
        JsonElement recentChanges = await filters.PollAsync(recent, rpc, CancellationToken.None);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(changes.EnumerateArray().Select(item => item.GetString()), Is.EqualTo(new[] { first.ToString(), second.ToString() }));
            Assert.That(recentChanges.EnumerateArray().Select(item => item.GetString()), Is.EqualTo(new[] { second.ToString() }));
        }

        for (int i = 0; i < RpcFilters.MaxPendingHashes; i++) filters.PublishPending(first);
        JsonElement full = await filters.PollAsync(recent, rpc, CancellationToken.None);
        filters.PublishPending(second);
        RpcException? overflowed = await Assert.ThrowsAsync<RpcException>(async () => await filters.PollAsync(slow, rpc, CancellationToken.None));
        JsonElement last = await filters.PollAsync(recent, rpc, CancellationToken.None);
        Assert.That(last.GetArrayLength(), Is.EqualTo(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(full.GetArrayLength(), Is.EqualTo(RpcFilters.MaxPendingHashes));
            Assert.That(overflowed!.Code, Is.EqualTo(ErrorCodes.LimitExceeded));
            Assert.That(last[0].GetString(), Is.EqualTo(second.ToString()));
        }
    }

    private static RpcBackend Backend() => new(Substitute.For<IExecutionStateSource>(), static () => throw new InvalidOperationException(), 1);

    private sealed class ManualClock : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        internal void Advance(TimeSpan elapsed) => _timestamp += elapsed.Ticks;
    }
}
