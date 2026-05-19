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
    bool TryRequestCells(Hash256 hash, BlobCellMask requestMask, PublicKey preferredPeerId);
    int RequestCellsForCustodyChange(BlobCellMask newCustodyMask, bool requestAllAnnouncedCells);
    bool HasRecordedTransaction(Hash256 hash);
    int GetFullProviderAnnouncementCount(Hash256 hash);
    AcceptTxResult? RecordTransaction(ISparseBlobPoolPeer peer, Transaction transaction);
    bool RecordCells(ISparseBlobPoolPeer peer, Hash256 hash, BlobCellMask cellMask, byte[][] cells);
    void Clear(Hash256 hash);
}
