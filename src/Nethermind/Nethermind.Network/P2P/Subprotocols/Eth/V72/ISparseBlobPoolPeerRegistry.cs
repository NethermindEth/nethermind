// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.TxPool;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V72;

/// <summary>
/// Coordinates node-wide sparse blob announcements, requests, admission, and serving budgets across eth/72 peers.
/// </summary>
/// <remarks>
/// Sparse request ownership requires delegation to <see cref="SparseBlobPoolPeerRegistry"/>.
/// Forwarding decorators are supported; independent implementations are not supported because
/// restoration, body-attempt and cell-claim tokens belong to the production registry.
/// Decorators must forward the original tokens and borrowed leases without replacing or disposing them.
/// </remarks>
public interface ISparseBlobPoolPeerRegistry
{
    /// <summary>Registers an active eth/72 peer.</summary>
    void AddPeer(ISparseBlobPoolPeer peer);

    /// <summary>Removes a peer and releases its tracked sparse blob state.</summary>
    void RemovePeer(ISparseBlobPoolPeer peer);

    /// <summary>Records an announcement when the peer and process admission budgets allow it.</summary>
    /// <returns><c>true</c> when the announcement is tracked; otherwise <c>false</c>.</returns>
    bool RecordAnnouncement(ISparseBlobPoolPeer peer, in ValueHash256 hash, BlobCellMask announcementMask);

    /// <summary>Returns the node-wide provider or sampler request mask for an announcement.</summary>
    BlobCellMask GetRequestMask(in ValueHash256 hash, BlobCellMask announcementMask, int providerProbabilityPercent);

    /// <summary>Temporarily removes an announcement and retains a conditional restoration.</summary>
    /// <returns><c>false</c> on local ticket capacity refusal; otherwise <c>true</c>.</returns>
    /// <remarks>A supplied request ownership is borrowed and must match the original registry, state, peer and reservation revision. Stale or foreign ownership cannot change current state. Decorators must forward it without disposing it.</remarks>
    bool TryBeginAnnouncementRestore(ISparseBlobPoolPeer peer, Hash256 hash, out AnnouncementRestoration? restoration, IDisposable? requestOwnership = null);

    /// <summary>Restores an owned announcement, preserving any fresh announcement already present.</summary>
    bool TryRestoreAnnouncement(ISparseBlobPoolPeer peer, Hash256 hash, AnnouncementRestoration restoration);

    /// <summary>Removes an announcement and invalidates its outstanding restoration.</summary>
    /// <returns>The canceled restoration, or <c>null</c> when none existed.</returns>
    /// <remarks>A supplied request ownership is borrowed and must match the original registry, state, peer and reservation revision. Stale or foreign ownership cannot change current state. Decorators must forward it without disposing it.</remarks>
    AnnouncementRestoration? RemoveAnnouncementPermanently(ISparseBlobPoolPeer peer, Hash256 hash, IDisposable? requestOwnership = null);

    /// <summary>Captures body request ownership against the current tracked transaction.</summary>
    /// <returns>An unarmed attempt, or <c>null</c> when the peer or state is no longer active.</returns>
    BodyRequestAttempt? CreateBodyRequestAttempt(ISparseBlobPoolPeer peer, Hash256 hash);

    /// <inheritdoc cref="CreateBodyRequestAttempt(ISparseBlobPoolPeer, Hash256)"/>
    BodyRequestAttempt? CreateBodyRequestAttempt(ISparseBlobPoolPeer peer, in ValueHash256 hash) =>
        CreateBodyRequestAttempt(peer, hash.ToHash256());

    /// <summary>Excludes a peer from body retries for an omitted requested transaction.</summary>
    /// <returns>The canceled announcement restoration, when one existed.</returns>
    /// <remarks>A supplied original attempt is borrowed and must match its owner, tracked state, peer and current confirmed generation. Completion does not consume omission ownership; each omission is applied once.</remarks>
    AnnouncementRestoration? RecordBodyUnavailable(ISparseBlobPoolPeer peer, Hash256 hash, BodyRequestAttempt? requestAttempt = null, bool retainCellAnnouncement = false);

    /// <summary>Gets whether the peer announced every column in the requested mask.</summary>
    /// <remarks>Registry decorators must forward coverage so covered underdelivery remains attributable.</remarks>
    bool CoversRequest(ISparseBlobPoolPeer peer, ValueHash256 hash, BlobCellMask mask);

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
    bool TryRequestCells(in ValueHash256 hash, BlobCellMask requestMask, PublicKey lastResortPeerId);

    /// <summary>
    /// Marks previously requested cells as no longer in flight once a response
    /// (successful, partial, or empty) has been processed.
    /// </summary>
    void OnCellsRequestCompleted(in ValueHash256 hash, BlobCellMask completedMask, ISparseBlobPoolPeer peer);

    /// <summary>Gets whether the transaction body has been recorded for sparse admission.</summary>
    bool HasRecordedTransaction(in ValueHash256 hash);

    /// <summary>Gets the number of active peers that announced full cell availability for a transaction.</summary>
    int GetFullProviderAnnouncementCount(in ValueHash256 hash);

    /// <summary>Records and validates an elided blob transaction announced by a peer.</summary>
    /// <returns>The admission result when submission completed; otherwise <c>null</c>.</returns>
    AcceptTxResult? RecordTransaction(ISparseBlobPoolPeer peer, Transaction transaction);

    /// <summary>Records cells attributed to a peer for later validation and admission.</summary>
    /// <returns><c>true</c> when the cells were retained; otherwise <c>false</c>.</returns>
    /// <remarks>A supplied request ownership is borrowed and must match the original registry, state, peer and reservation revision. Stale or foreign ownership cannot change current state. Decorators must forward it without disposing it.</remarks>
    bool RecordCells(ISparseBlobPoolPeer peer, Hash256 hash, BlobCellMask cellMask, byte[][] cells, IDisposable? requestOwnership = null);

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
    void Clear(in ValueHash256 hash);
}
