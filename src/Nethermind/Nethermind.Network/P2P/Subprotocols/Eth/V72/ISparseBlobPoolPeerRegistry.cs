// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.TxPool;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V72;

public interface ISparseBlobPoolPeerRegistry
{
    void AddPeer(ISparseBlobPoolPeer peer);
    void RemovePeer(PublicKey peerId);
    void RecordAnnouncement(ISparseBlobPoolPeer peer, Hash256 hash, BlobCellMask announcementMask);
    /// <summary>
    /// Forgets a single peer's announcement for a transaction, e.g. after the peer answered
    /// a cell request with an empty response, so retries converge on other providers.
    /// </summary>
    void RemoveAnnouncement(PublicKey peerId, Hash256 hash);
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
    void OnCellsRequestCompleted(Hash256 hash, BlobCellMask completedMask);
    bool HasRecordedTransaction(Hash256 hash);
    int GetFullProviderAnnouncementCount(Hash256 hash);
    AcceptTxResult? RecordTransaction(ISparseBlobPoolPeer peer, Transaction transaction);
    bool RecordCells(ISparseBlobPoolPeer peer, Hash256 hash, BlobCellMask cellMask, byte[][] cells);
    void Clear(Hash256 hash);
}
