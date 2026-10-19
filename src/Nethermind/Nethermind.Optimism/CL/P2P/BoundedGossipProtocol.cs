// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Libp2p.Core;

namespace Nethermind.Optimism.CL.P2P;

/// <summary>Reserves an inbound RPC allowance before the library reads or decodes gossip.</summary>
/// <remarks>The library reads one RPC at a time per stream. All protocol versions share the same allowance,
/// including duplicate streams on different sessions of one peer.</remarks>
internal sealed class BoundedGossipProtocol(ISessionProtocol inner, GossipStreamBudget budget) : ISessionProtocol
{
    public string Id => inner.Id;

    public Task DialAsync(IChannel channel, ISessionContext context) => inner.DialAsync(channel, context);

    public async Task ListenAsync(IChannel channel, ISessionContext context)
    {
        if (context.State.RemotePeerId is not { } peer || !budget.TryAcquire(peer))
        {
            await channel.CloseAsync();
            return;
        }

        try
        {
            await inner.ListenAsync(channel, context);
        }
        finally
        {
            budget.Release(peer);
        }
    }
}

internal sealed class GossipStreamBudget
{
    internal const int MaxStreams = 16;
    private readonly HashSet<PeerId> _peers = [];
    private readonly System.Threading.Lock _lock = new();

    internal bool TryAcquire(PeerId peer)
    {
        lock (_lock)
        {
            return _peers.Count < MaxStreams && _peers.Add(peer);
        }
    }

    internal void Release(PeerId peer)
    {
        lock (_lock)
        {
            _peers.Remove(peer);
        }
    }
}
