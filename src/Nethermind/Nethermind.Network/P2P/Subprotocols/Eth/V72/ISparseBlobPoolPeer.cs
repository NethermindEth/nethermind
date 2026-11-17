// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Stats.Model;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V72;

/// <summary>Represents an eth/72 peer participating in sparse blob transaction and cell exchange.</summary>
public interface ISparseBlobPoolPeer
{
    /// <summary>Gets the remote peer identity.</summary>
    PublicKey Id { get; }
    /// <summary>Gets whether the peer session is closing.</summary>
    bool IsClosing { get; }
    /// <summary>Gets whether this peer is currently eligible for cell requests.</summary>
    /// <remarks>Decorators must forward the wrapped peer's backoff eligibility.</remarks>
    bool CanRequestCells => !IsClosing;
    /// <summary>Gets the session identity captured with cells awaiting validation.</summary>
    /// <remarks>Decorators must forward this identity and useful-cell credit to the same wrapped session.</remarks>
    object CellSourceToken => this;
    /// <summary>Records verified, newly retained cells from this session.</summary>
    /// <remarks>Decorators must forward credit to the originating session after cell validation.</remarks>
    void RecordUsefulCells(ValueHash256 hash);
    /// <summary>Attempts to request the specified cells from this peer.</summary>
    bool TrySendGetCells(Hash256 hash, BlobCellMask requestMask);
    /// <summary>Attempts to request cells with ownership bound to the originating reservation.</summary>
    /// <remarks>Invoke the ownership factory only when claiming a reply. Dispose its lease after processing. Decorators must forward this overload; the default sends without retaining reservation ownership.</remarks>
    bool TrySendGetCells(Hash256 hash, BlobCellMask requestMask, Func<(IDisposable Lease, Action<BlobCellMask> Complete)> claimReservation)
        => TrySendGetCells(hash, requestMask);
    /// <summary>Gets whether this peer is eligible for transaction body requests.</summary>
    /// <remarks>Decorators must forward the wrapped peer's body-backoff eligibility.</remarks>
    bool CanRequestTransactions { get; }
    /// <summary>Records a structurally validated transaction body from this session.</summary>
    void RecordUsefulBody(ValueHash256 hash);
    /// <summary>Attempts to request a pooled transaction from this peer.</summary>
    bool TrySendPooledTransactionRequest(Hash256 hash);
    /// <summary>Expires bounded per-peer sparse-blob state at the supplied registry time.</summary>
    void MaintainSparseBlobState(DateTimeOffset now);
    /// <summary>Disconnects the peer for a sparse-blob protocol violation.</summary>
    void DisconnectSparseBlobPeer(DisconnectReason reason, string details);
}

internal interface ICellRequestOwnership : IDisposable
{
    bool IsCurrent { get; }
}
