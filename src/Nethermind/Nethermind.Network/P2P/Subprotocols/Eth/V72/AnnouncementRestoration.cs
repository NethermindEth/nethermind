// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V72;

/// <summary>A conditional restoration of a temporarily removed peer announcement.</summary>
public sealed class AnnouncementRestoration
{
    private int _valid = 1;

    internal AnnouncementRestoration(ISparseBlobPoolPeer peer, object state, BlobCellMask mask, DateTimeOffset createdAt)
    {
        Peer = peer;
        State = state;
        Mask = mask;
        CreatedAt = createdAt;
    }

    /// <summary>Gets the original announcement mask.</summary>
    public BlobCellMask Mask { get; }
    internal ISparseBlobPoolPeer Peer { get; }
    internal object State { get; }
    internal DateTimeOffset CreatedAt { get; }
    internal bool IsValid => Volatile.Read(ref _valid) != 0;
    internal bool Invalidate() => Interlocked.Exchange(ref _valid, 0) != 0;
}
