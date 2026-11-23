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
        JsonElement empty = await DeliveredPollAsync(filters, id, rpc);
        Assert.That(empty.GetArrayLength(), Is.Zero);
        clock.Advance(TimeSpan.FromTicks(1));
        UInt256 replacement = filters.Install();
        Assert.That((await DeliveredPollAsync(filters, id, rpc)).GetArrayLength(), Is.Zero);

        clock.Advance(RpcFilters.IdleTimeout);
        RpcException? expired = await Assert.ThrowsAsync<RpcException>(async () => await DeliveredPollAsync(filters, id, rpc));
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
            JsonElement changes = await DeliveredPollAsync(filters, active, rpc);
            Assert.That(changes.EnumerateArray().Select(item => item.GetString()), Is.EqualTo(new[] { hash.ToString() }));
        }

        RpcException? overflowed = await Assert.ThrowsAsync<RpcException>(async () => await DeliveredPollAsync(filters, slow, rpc));
        JsonElement consumed = await DeliveredPollAsync(filters, active, rpc);
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

        JsonElement changes = await DeliveredPollAsync(filters, slow, rpc);
        JsonElement recentChanges = await DeliveredPollAsync(filters, recent, rpc);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(changes.EnumerateArray().Select(item => item.GetString()), Is.EqualTo(new[] { first.ToString(), second.ToString() }));
            Assert.That(recentChanges.EnumerateArray().Select(item => item.GetString()), Is.EqualTo(new[] { second.ToString() }));
        }

        for (int i = 0; i < RpcFilters.MaxPendingHashes; i++) filters.PublishPending(first);
        JsonElement full = await DeliveredPollAsync(filters, recent, rpc);
        filters.PublishPending(second);
        RpcException? overflowed = await Assert.ThrowsAsync<RpcException>(async () => await DeliveredPollAsync(filters, slow, rpc));
        JsonElement last = await DeliveredPollAsync(filters, recent, rpc);
        Assert.That(last.GetArrayLength(), Is.EqualTo(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(full.GetArrayLength(), Is.EqualTo(RpcFilters.MaxPendingHashes));
            Assert.That(overflowed!.Code, Is.EqualTo(ErrorCodes.LimitExceeded));
            Assert.That(last[0].GetString(), Is.EqualTo(second.ToString()));
        }
    }

    private static async Task<JsonElement> DeliveredPollAsync(RpcFilters filters, UInt256 id, RpcBackend rpc)
    {
        using ResultWrapper<SerializedJson> response = await filters.PollAsync(id, rpc, CancellationToken.None);
        if (response is RpcFilters.PollResponse poll) poll.TakeCompletion()?.Invoke(true);
        return JsonElement.Parse(response.Data.Utf8Json);
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
