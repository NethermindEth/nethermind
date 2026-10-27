// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Stats.Model;
using Nethermind.TxPool;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V72;

/// <summary>
/// EIP-8437 blob-carrying dependency-bearing frame transactions. Their body arrives only inside a validated proof wrapper,
/// with blob data elided; EIP-8070 cell retrieval then establishes availability before the pool admits them. Cells are never
/// requested for one before its covering wrapper validated, and it is never admitted outside this path.
/// </summary>
/// <remarks>Each held body is charged, like a sparse transaction body, to the node-wide and source-peer byte budgets, and
/// counted per source peer and per sender; a source peer's bodies are dropped when it disconnects. A body nobody has announced
/// cells for expires sooner than one with providers.</remarks>
public sealed partial class SparseBlobPoolPeerRegistry
{
    private const int MaxWrappedTransactions = 1024;
    private const int MaxWrappedTransactionsPerPeer = 256;
    private const int MaxWrappedTransactionsPerSender = 16;
    private static readonly TimeSpan WrappedTransactionLifetime = MaxTrackedStateLifetime;
    private static readonly TimeSpan UnannouncedWrappedTransactionLifetime = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<ValueHash256, WrappedBlobTx> _wrapped = new();
    private readonly Dictionary<PublicKey, int> _wrappedPerPeer = [];
    private readonly Dictionary<AddressAsKey, int> _wrappedPerSender = [];

    internal int WrappedCount => _wrapped.Count;

    internal bool IsAwaitingCells(Hash256 hash) => _wrapped.ContainsKey(hash.ValueHash256);

    /// <inheritdoc/>
    public AcceptTxResult Admit(Transaction transaction, object? source, IDisposable proofPin)
    {
        Hash256? hash = transaction.Hash;
        if (Volatile.Read(ref _disposed) != 0 || hash is null || transaction.SenderAddress is null || !transaction.SupportsFrames
            || transaction.NetworkWrapper is not ShardBlobNetworkWrapper { Version: ProofVersion.V1, Blobs.Length: 0 } wrapper)
        {
            proofPin.Dispose();
            return AcceptTxResult.Invalid.WithMessage("Not a blob-carrying frame transaction in its elided network form.");
        }

        ISparseBlobPoolPeer? peer = source as ISparseBlobPoolPeer;
        PublicKey? sourceId = peer is not null && IsActivePeer(peer) ? peer.Id : null;
        if (_wrapped.TryGetValue(hash.ValueHash256, out WrappedBlobTx? existing))
        {
            // Already awaiting its cells under an earlier wrapper: this source keeps it alive too.
            proofPin.Dispose();
            if (sourceId is not null && existing.AddSource(sourceId))
            {
                DropIfGone(hash.ValueHash256, existing, peer!);
            }

            return AcceptTxResult.Accepted;
        }

        AcceptTxResult valid = _txPool.ValidateTxForBlobSampling(transaction);
        if (!valid)
        {
            proofPin.Dispose();
            return valid;
        }

        int bytes = transaction.GetLength();
        if (_wrapped.Count >= MaxWrappedTransactions || !TryReserveWrapped(sourceId, transaction.SenderAddress, hash.ValueHash256, bytes))
        {
            proofPin.Dispose();
            return AcceptTxResult.IncompleteBlobData.WithMessage("Too many blob transactions await cell retrieval; retry later.");
        }

        DateTimeOffset now = _timestamper.UtcNowOffset;
        WrappedBlobTx entry = new(transaction, wrapper, proofPin, sourceId, transaction.SenderAddress, bytes, now + WrappedTransactionLifetime,
            now + UnannouncedWrappedTransactionLifetime);
        if (!_wrapped.TryAdd(hash.ValueHash256, entry))
        {
            ReleaseWrapped(entry, hash.ValueHash256);
            proofPin.Dispose();
            return Admit(transaction, source, NoPin.Instance);
        }

        // The source may have disconnected while this was added, after its bodies were dropped.
        if (peer is not null && sourceId is not null && DropIfGone(hash.ValueHash256, entry, peer))
        {
            return AcceptTxResult.Accepted;
        }

        RecordWrapped(hash, entry, peer);
        return AcceptTxResult.Accepted;
    }

    /// <summary>Drops <paramref name="peer"/> as a source of <paramref name="entry"/> if it has disconnected.</summary>
    private bool DropIfGone(in ValueHash256 hash, WrappedBlobTx entry, ISparseBlobPoolPeer peer)
    {
        if (IsActivePeer(peer))
        {
            return false;
        }

        DropSource(hash, entry, peer.Id);
        return true;
    }

    /// <summary>Records the wrapped body for cell admission, attributed to <paramref name="preferred"/> when it is an active
    /// peer that announced the cells, else to any active peer that did; without one it waits for the first cell announcement.</summary>
    private void RecordWrapped(Hash256 hash, WrappedBlobTx entry, ISparseBlobPoolPeer? preferred)
    {
        if (!_transactions.TryGetValue(hash.ValueHash256, out TrackedSparseBlobTx? state))
        {
            return;
        }

        ISparseBlobPoolPeer? peer = null;
        lock (state.Lock)
        {
            if (preferred is not null && state.Announcements.ContainsKey(preferred.Id) && IsActivePeer(preferred))
            {
                peer = preferred;
            }
            else
            {
                foreach (PublicKey announcer in state.Announcements.Keys)
                {
                    if (!state.AmbiguousFailureSources.Contains(announcer) && _peers.TryGetValue(announcer, out ISparseBlobPoolPeer? candidate)
                        && !candidate.IsClosing)
                    {
                        peer = candidate;
                        break;
                    }
                }
            }
        }

        if (peer is not null)
        {
            entry.MarkAnnounced();
            RecordTransaction(peer, entry.CreateBody());
        }
    }

    /// <summary>Hands the wrapped body back to admission once a peer announces its cells.</summary>
    private void OnFrameCellAnnouncement(ISparseBlobPoolPeer peer, Hash256 hash)
    {
        if (_wrapped.TryGetValue(hash.ValueHash256, out WrappedBlobTx? entry) && !HasRecordedTransaction(hash))
        {
            RecordWrapped(hash, entry, peer);
        }
    }

    /// <summary>Recovers a transaction body after a failed admission: a wrapped body is recorded again; any other is requested
    /// from <paramref name="peer"/>.</summary>
    private void RequestTransactionBody(ISparseBlobPoolPeer? peer, Hash256 hash)
    {
        if (_wrapped.TryGetValue(hash.ValueHash256, out WrappedBlobTx? entry))
        {
            if (!HasRecordedTransaction(hash))
            {
                RecordWrapped(hash, entry, peer);
            }

            return;
        }

        peer?.TrySendPooledTransactionRequest(hash);
    }

    /// <summary>Whether cells may be requested. Once the body is known its type decides: a dependency-bearing frame transaction
    /// needs its validated covering wrapper. Before that, cells announced as a frame transaction's wait for its body, and a peer's
    /// type-3 claim opens retrieval only from peers that made that claim; one peer's claimed type never gates another's.</summary>
    /// <param name="typeThreeClaimersOnly">Whether only announcers that claimed a type-3 transaction may serve the cells.</param>
    private bool MayRequestCells(Hash256 hash, TrackedSparseBlobTx state, out bool typeThreeClaimersOnly)
    {
        typeThreeClaimersOnly = false;
        if (_wrapped.ContainsKey(hash.ValueHash256) || _txPool.TryGetPendingBlobCellMask(hash, out _))
        {
            return true;
        }

        lock (state.Lock)
        {
            if (state.BodyType is { } bodyType)
            {
                return bodyType != TxType.FrameTx || !state.BodyDependencyBearing;
            }

            if (state.Announcements.Count == 0)
            {
                return true;
            }

            typeThreeClaimersOnly = true;
            foreach (PublicKey announcer in state.Announcements.Keys)
            {
                if (ClaimedType(state, announcer) != TxType.FrameTx)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>Under <c>state.Lock</c>, adds the announcers that claimed a frame transaction to <paramref name="excluded"/>.</summary>
    private static void ExcludeFrameClaimers(TrackedSparseBlobTx state, ref HashSet<PublicKey>? excluded)
    {
        foreach (PublicKey announcer in state.Announcements.Keys)
        {
            if (ClaimedType(state, announcer) == TxType.FrameTx)
            {
                (excluded ??= []).Add(announcer);
            }
        }
    }

    private static TxType ClaimedType(TrackedSparseBlobTx state, PublicKey announcer)
        => state.AnnouncedTypes.TryGetValue(announcer, out TxType type) ? type : TxType.Blob;

    private static TxType ClaimedTypeLocked(TrackedSparseBlobTx state, PublicKey announcer)
    {
        lock (state.Lock)
        {
            return ClaimedType(state, announcer);
        }
    }

    /// <summary>Records the body's type and disconnects announcers that claimed another: an announcement's type is part of its
    /// metadata, and a false one could otherwise steer cell retrieval.</summary>
    private void RecordBodyType(Hash256 hash, TrackedSparseBlobTx state, Transaction body)
    {
        List<PublicKey>? mismatched = null;
        lock (state.Lock)
        {
            if (!IsCurrentState(hash, state))
            {
                return;
            }

            state.BodyType = body.Type;
            state.BodyDependencyBearing = Eip8288Dependencies.IsDependencyBearing(body);
            foreach (PublicKey announcer in state.Announcements.Keys)
            {
                if (ClaimedType(state, announcer) != body.Type)
                {
                    (mismatched ??= []).Add(announcer);
                }
            }
        }

        foreach (PublicKey announcer in mismatched ?? [])
        {
            DisconnectMismatchedAnnouncer(announcer, hash);
        }
    }

    private void DisconnectMismatchedAnnouncer(PublicKey announcer, Hash256 hash)
    {
        if (_peers.TryGetValue(announcer, out ISparseBlobPoolPeer? peer))
        {
            peer.DisconnectSparseBlobPeer(DisconnectReason.BreachOfProtocol, $"announced cells of {hash} under another transaction type");
            RemovePeer(peer);
        }
    }

    /// <summary>Submits a wrapped transaction under the coverage its wrapper pinned, releasing the pin once the pool has
    /// admitted or refused it.</summary>
    private AcceptTxResult SubmitWrappedTransaction(Transaction transaction)
    {
        // EIP-8437: a dependency-bearing transaction is admitted only through a validated proof wrapper.
        if (transaction.Hash is not { } hash || !_wrapped.ContainsKey(hash.ValueHash256))
        {
            return AcceptTxResult.MissingDependencyProof;
        }

        AcceptTxResult result = _txPool.SubmitTx(transaction, TxHandlingOptions.None);
        // Invalid cells say nothing of the proof: the body stays for cells from another provider.
        if (result != AcceptTxResult.InvalidBlobProofs
            && (result != AcceptTxResult.AlreadyKnown || _txPool.TryGetPendingBlobTransaction(hash, out _)))
        {
            RemoveWrapped(hash.ValueHash256);
        }

        return result;
    }

    private void RemoveWrapped(in ValueHash256 hash)
    {
        if (_wrapped.TryRemove(hash, out WrappedBlobTx? entry))
        {
            ReleaseWrapped(entry, hash);
            entry.ProofPin.Dispose();
        }
    }

    private bool TryReserveWrapped(PublicKey? peerId, Address sender, in ValueHash256 hash, int bytes)
    {
        lock (_accountingLock)
        {
            if (_trackedTransactionBytes + bytes > MaxTrackedTransactionBytes
                || _wrappedPerSender.GetValueOrDefault(sender) >= MaxWrappedTransactionsPerSender
                || peerId is not null && !TryChargePeerLocked(peerId, hash, bytes))
            {
                return false;
            }

            _trackedTransactionBytes += bytes;
            _wrappedPerSender[sender] = _wrappedPerSender.GetValueOrDefault(sender) + 1;
            return true;
        }
    }

    /// <summary>Charges a held body to <paramref name="peerId"/>'s byte budget and wrapped-body count; under the accounting lock.</summary>
    private bool TryChargePeerLocked(PublicKey peerId, in ValueHash256 hash, int bytes)
    {
        PeerUsage usage = GetPeerUsage(peerId);
        if (usage.TransactionBytes + bytes > MaxTrackedTransactionBytesPerPeer
            || _wrappedPerPeer.GetValueOrDefault(peerId) >= MaxWrappedTransactionsPerPeer)
        {
            RemovePeerUsageIfEmpty(peerId, usage);
            return false;
        }

        usage.TransactionBytes += bytes;
        AddTrackedHashReference(usage, hash);
        _wrappedPerPeer[peerId] = _wrappedPerPeer.GetValueOrDefault(peerId) + 1;
        return true;
    }

    private void ReleasePeerChargeLocked(PublicKey peerId, in ValueHash256 hash, int bytes)
    {
        Decrement(_wrappedPerPeer, peerId);
        if (_peerUsage.TryGetValue(peerId, out PeerUsage? usage))
        {
            usage.TransactionBytes -= bytes;
            ReleaseTrackedHashReference(usage, hash);
            RemovePeerUsageIfEmpty(peerId, usage);
        }
    }

    private void ReleaseWrapped(WrappedBlobTx entry, in ValueHash256 hash)
    {
        lock (entry)
        {
            lock (_accountingLock)
            {
                _trackedTransactionBytes -= entry.Bytes;
                Decrement(_wrappedPerSender, entry.Sender);
                if (entry.ChargedPeer is { } peerId)
                {
                    ReleasePeerChargeLocked(peerId, hash, entry.Bytes);
                    entry.ChargedPeer = null;
                }
            }
        }
    }

    private static void Decrement<TKey>(Dictionary<TKey, int> counts, TKey key) where TKey : notnull
    {
        int count = counts.GetValueOrDefault(key) - 1;
        if (count <= 0) counts.Remove(key);
        else counts[key] = count;
    }

    /// <summary>Removes a disconnected peer as a source of the bodies its wrappers supplied; a body is dropped once no source
    /// remains, and its charge moves to a remaining source.</summary>
    private void DropWrapped(PublicKey peerId)
    {
        foreach (KeyValuePair<ValueHash256, WrappedBlobTx> entry in _wrapped)
        {
            DropSource(entry.Key, entry.Value, peerId);
        }
    }

    private void DropSource(in ValueHash256 hash, WrappedBlobTx entry, PublicKey peerId)
    {
        bool remove;
        lock (entry)
        {
            if (!entry.Sources.Remove(peerId))
            {
                return;
            }

            remove = entry.Sources.Count == 0;
            if (!remove && entry.ChargedPeer == peerId)
            {
                lock (_accountingLock)
                {
                    ReleasePeerChargeLocked(peerId, hash, entry.Bytes);
                    entry.ChargedPeer = null;
                    foreach (PublicKey remaining in entry.Sources)
                    {
                        if (TryChargePeerLocked(remaining, hash, entry.Bytes))
                        {
                            entry.ChargedPeer = remaining;
                            break;
                        }
                    }
                }
            }
        }

        if (remove && _wrapped.TryGetValue(hash, out WrappedBlobTx? current) && ReferenceEquals(current, entry))
        {
            RemoveWrapped(hash);
        }
    }

    private void MaintainWrapped(DateTimeOffset now)
    {
        foreach (KeyValuePair<ValueHash256, WrappedBlobTx> entry in _wrapped)
        {
            if (entry.Value.IsExpired(now) || _txPool.TryGetPendingBlobTransaction(entry.Key.ToHash256(), out _))
            {
                RemoveWrapped(entry.Key);
            }
        }
    }

    private void DisposeWrapped()
    {
        foreach (KeyValuePair<ValueHash256, WrappedBlobTx> entry in _wrapped)
        {
            RemoveWrapped(entry.Key);
        }
    }

    /// <summary>A body from a validated wrapper, its pinned proof coverage, the connections that supplied it, its charges and
    /// its deadlines. Sources and the charged peer are guarded by the entry itself.</summary>
    private sealed class WrappedBlobTx(Transaction body, ShardBlobNetworkWrapper wrapper, IDisposable proofPin, PublicKey? source, Address sender,
        int bytes, DateTimeOffset expiresAt, DateTimeOffset unannouncedExpiresAt)
    {
        private int _announced;

        public IDisposable ProofPin { get; } = proofPin;
        public Address Sender { get; } = sender;
        public int Bytes { get; } = bytes;

        /// <summary>Peers whose validated wrappers supplied the body; empty for a local one, which no disconnect drops.</summary>
        public HashSet<PublicKey> Sources { get; } = source is null ? [] : [source];

        /// <summary>The source whose budgets the body is charged to, if any.</summary>
        public PublicKey? ChargedPeer { get; set; } = source;

        public bool AddSource(PublicKey peerId)
        {
            lock (this)
            {
                return Sources.Count != 0 && Sources.Add(peerId);
            }
        }

        public void MarkAnnounced() => Volatile.Write(ref _announced, 1);

        public bool IsExpired(DateTimeOffset now) => now >= (Volatile.Read(ref _announced) != 0 ? expiresAt : unannouncedExpiresAt);

        /// <summary>A copy for one admission attempt, which attaches cells to its own network wrapper.</summary>
        public Transaction CreateBody()
        {
            Transaction copy = new();
            body.CopyTo(copy, copyHash: true);
            copy.NetworkWrapper = wrapper;
            copy.ClearLengthCache();
            return copy;
        }
    }

    private sealed class NoPin : IDisposable
    {
        public static readonly NoPin Instance = new();
        public void Dispose() { }
    }
}
