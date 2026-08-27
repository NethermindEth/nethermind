// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.TxPool;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V72;

/// <summary>
/// Coordinates node-wide sparse blob announcements, requests, admission, and serving budgets across eth/72 peers.
/// </summary>
public interface ISparseBlobPoolPeerRegistry
{
    /// <summary>Registers an active eth/72 peer.</summary>
    void AddPeer(ISparseBlobPoolPeer peer);

    /// <summary>Removes a peer and releases its tracked sparse blob state.</summary>
    void RemovePeer(ISparseBlobPoolPeer peer);

    /// <summary>Records an announcement when the peer and process admission budgets allow it.</summary>
    /// <returns><c>true</c> when the announcement is tracked; otherwise <c>false</c>.</returns>
    bool RecordAnnouncement(ISparseBlobPoolPeer peer, Hash256 hash, BlobCellMask announcementMask);

    /// <summary>Returns the node-wide provider or sampler request mask for an announcement.</summary>
    BlobCellMask GetRequestMask(Hash256 hash, BlobCellMask announcementMask, int providerProbabilityPercent);

    /// <summary>
    /// Forgets a single peer's announcement for a transaction, e.g. after the peer answered
    /// a cell request with an empty response, so retries converge on other providers.
    /// </summary>
    /// <returns>
    /// The mask the peer had announced, so a caller that backs the peer off temporarily can restore
    /// it later; <see cref="BlobCellMask.Empty"/> when the peer had no announcement.
    /// </returns>
    BlobCellMask RemoveAnnouncement(ISparseBlobPoolPeer peer, Hash256 hash);

    /// <summary>
    /// Requests cells from a randomly selected announcing peer. Cells already held in the local
    /// pool or still in flight are not re-requested.
    /// </summary>
    /// <param name="lastResortPeerId">
    /// Peer used only when no other announced provider is available — typically the caller itself
    /// or the peer whose previous response failed, to avoid leaning on the same source again.
    /// </param>
    /// <returns>
    /// <c>true</c> when a request was sent or nothing is left to fetch;
    /// <c>false</c> when cells are needed but no provider is available.
    /// </returns>
    bool TryRequestCells(Hash256 hash, BlobCellMask requestMask, PublicKey lastResortPeerId);

    /// <summary>
    /// Marks previously requested cells as no longer in flight once a response
    /// (successful, partial, or empty) has been processed.
    /// </summary>
    void OnCellsRequestCompleted(Hash256 hash, BlobCellMask completedMask, ISparseBlobPoolPeer peer);

    /// <summary>Gets whether the transaction body has been recorded for sparse admission.</summary>
    bool HasRecordedTransaction(Hash256 hash);

    /// <summary>Gets the number of active peers that announced full cell availability for a transaction.</summary>
    int GetFullProviderAnnouncementCount(Hash256 hash);

    /// <summary>Records and validates an elided blob transaction announced by a peer.</summary>
    /// <returns>The admission result when submission completed; otherwise <c>null</c>.</returns>
    AcceptTxResult? RecordTransaction(ISparseBlobPoolPeer peer, Transaction transaction);

    /// <summary>Records cells attributed to a peer for later validation and admission.</summary>
    /// <returns><c>true</c> when the cells were retained; otherwise <c>false</c>.</returns>
    bool RecordCells(ISparseBlobPoolPeer peer, Hash256 hash, BlobCellMask cellMask, byte[][] cells);

    /// <summary>Attempts to validate and merge recorded cells into the transaction pool.</summary>
    /// <returns><c>true</c> when recorded cells were applied or are already present; otherwise <c>false</c>.</returns>
    bool TryApplyRecordedCells(Hash256 hash);

    /// <summary>Attempts to reserve node-wide serving capacity for the specified cell-equivalent work.</summary>
    /// <returns><c>true</c> when capacity was reserved; otherwise <c>false</c>.</returns>
    bool TryAcquireCellServeWork(int work);

    /// <summary>Refunds unused cell-equivalent serving work from an active operation.</summary>
    void RefundCellServeWork(int work);

    /// <summary>Releases one active cell-serving operation.</summary>
    void ReleaseCellServeWork();

    /// <summary>Clears all tracked sparse state for a transaction.</summary>
    void Clear(Hash256 hash);
}
