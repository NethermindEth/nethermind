// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Stats.Model;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V72;

public interface ISparseBlobPoolPeer
{
    /// <summary>Gets the remote peer identity.</summary>
    PublicKey Id { get; }
    /// <summary>Gets whether the peer session is closing.</summary>
    bool IsClosing { get; }
    /// <summary>Attempts to request the specified cells from this peer.</summary>
    bool TrySendGetCells(Hash256 hash, BlobCellMask requestMask);
    /// <summary>Attempts to request a pooled transaction from this peer.</summary>
    bool TrySendPooledTransactionRequest(Hash256 hash);
    /// <summary>Expires bounded per-peer sparse-blob state at the supplied registry time.</summary>
    void MaintainSparseBlobState(DateTimeOffset now);
    /// <summary>Disconnects the peer for a sparse-blob protocol violation.</summary>
    void DisconnectSparseBlobPeer(DisconnectReason reason, string details);
}
