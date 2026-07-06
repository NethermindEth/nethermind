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
    /// Requests cells from a randomly selected announcing peer.
    /// </summary>
    /// <param name="lastResortPeerId">
    /// Peer used only when no other announced provider is available — typically the caller itself
    /// or the peer whose previous response failed, to avoid leaning on the same source again.
    /// </param>
    bool TryRequestCells(Hash256 hash, BlobCellMask requestMask, PublicKey lastResortPeerId);
    bool HasRecordedTransaction(Hash256 hash);
    int GetFullProviderAnnouncementCount(Hash256 hash);
    AcceptTxResult? RecordTransaction(ISparseBlobPoolPeer peer, Transaction transaction);
    bool RecordCells(ISparseBlobPoolPeer peer, Hash256 hash, BlobCellMask cellMask, byte[][] cells);
    void Clear(Hash256 hash);
}
