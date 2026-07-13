// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.TxPool;
namespace Nethermind.Network.P2P.Subprotocols.Eth.V72;

public interface ISparseBlobPoolPeerRegistry
{
    void AddPeer(ISparseBlobPoolPeer peer);
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
    void RemoveAnnouncement(ISparseBlobPoolPeer peer, Hash256 hash);
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
    bool HasRecordedTransaction(Hash256 hash);
    int GetFullProviderAnnouncementCount(Hash256 hash);
    AcceptTxResult? RecordTransaction(ISparseBlobPoolPeer peer, Transaction transaction);
    bool RecordCells(ISparseBlobPoolPeer peer, Hash256 hash, BlobCellMask cellMask, byte[][] cells);
    bool TryApplyRecordedCells(Hash256 hash);
    bool TryAcquireCellServeWork(int work);
    void RefundCellServeWork(int work) { }
    void ReleaseCellServeWork();
    void Clear(Hash256 hash);
}
