// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Exceptions;

namespace Nethermind.Optimism.CL.P2P;

/// <summary>Reserves an inbound RPC allowance before the library reads or decodes an RPC body.</summary>
/// <remarks>The library reads one RPC at a time per stream. All protocol versions share the same allowance,
/// including duplicate streams on different sessions of one peer.
/// libp2p 1.1.0's PubsubProtocol awaits each ReadPrefixedProtobufAsync and processes it synchronously;
/// IReader reads the prefix through ReadVarintUlongAsync. No read outlives ListenAsync, so releasing
/// its last allowance in finally cannot race a later acquisition.
/// </remarks>
internal sealed class BoundedGossipProtocol(ISessionProtocol inner, GossipStreamBudget budget, TimeSpan? bodyReadTimeout = null) : ISessionProtocol
{
    public string Id => inner.Id;

    public Task DialAsync(IChannel channel, ISessionContext context) => inner.DialAsync(channel, context);

    public async Task ListenAsync(IChannel channel, ISessionContext context)
    {
        if (context.State.RemotePeerId is not { } peer)
        {
            await channel.CloseAsync();
            return;
        }

        BudgetedChannel bounded = new(channel, budget, peer, bodyReadTimeout ?? TimeSpan.FromSeconds(10));
        try
        {
            await inner.ListenAsync(bounded, context);
        }
        finally
        {
            bounded.Release();
        }
    }

    private sealed class BudgetedChannel(IChannel channel, GossipStreamBudget budget, PeerId peer, TimeSpan bodyReadTimeout) : IChannel
    {
        private bool _reserved;

        public async Task<ulong> ReadVarintUlongAsync(CancellationToken token)
        {
            // The listener has finished processing the previous RPC before requesting its next prefix.
            Release();
            ulong length = await channel.ReadVarintUlongAsync(token);
            // Duplicate outbound dials can leave an idle stream ahead of the stream selected to send RPCs.
            using CancellationTokenSource waiting = CancellationTokenSource.CreateLinkedTokenSource(token, channel.CancellationToken);
            await budget.AcquireAsync(peer, waiting.Token);
            _reserved = true;
            return length;
        }

        public void Release()
        {
            if (_reserved)
            {
                budget.Release(peer);
                _reserved = false;
            }
        }

        public async ValueTask<ReadResult> ReadAsync(int length, ReadBlockingMode blockingMode = ReadBlockingMode.WaitAll, CancellationToken token = default)
        {
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token, channel.CancellationToken);
            deadline.CancelAfter(bodyReadTimeout);
            try
            {
                return await channel.ReadAsync(length, blockingMode, deadline.Token);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                await channel.CloseAsync();
                throw new ChannelClosedException();
            }
        }
        public ValueTask<IOResult> WriteAsync(ReadOnlySequence<byte> bytes, CancellationToken token = default) => channel.WriteAsync(bytes, token);
        public ValueTask<IOResult> WriteEofAsync(CancellationToken token = default) => channel.WriteEofAsync(token);
        public ValueTask CloseAsync() => channel.CloseAsync();
        public ValueTask AbortAsync() => channel.AbortAsync();
        public ValueTask CloseReadAsync() => channel.CloseReadAsync();
        public TaskAwaiter GetAwaiter() => channel.GetAwaiter();
        public CancellationToken CancellationToken => channel.CancellationToken;
    }
}

internal sealed class GossipStreamBudget
{
    internal const int MaxStreams = 16;
    private readonly HashSet<PeerId> _peers = [];
    private readonly System.Threading.Lock _lock = new();
    private TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal async Task AcquireAsync(PeerId peer, CancellationToken token)
    {
        while (true)
        {
            Task released;
            lock (_lock)
            {
                token.ThrowIfCancellationRequested();
                if (_peers.Count < MaxStreams && _peers.Add(peer)) return;
                released = _released.Task;
            }

            await released.WaitAsync(token);
        }
    }

    internal void Release(PeerId peer)
    {
        lock (_lock)
        {
            if (_peers.Remove(peer))
            {
                TaskCompletionSource released = _released;
                _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
                released.TrySetResult();
            }
        }
    }
}
