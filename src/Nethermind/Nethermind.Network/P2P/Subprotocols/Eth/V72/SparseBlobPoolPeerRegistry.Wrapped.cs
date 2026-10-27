// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.TxPool;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V72;

/// <summary>
/// EIP-8437 blob-carrying dependency-bearing frame transactions. Their body arrives only inside a validated proof wrapper,
/// with blob data elided; EIP-8070 cell retrieval then establishes availability before the pool admits them. Cells are never
/// requested for one before its covering wrapper validated, and it is never admitted outside this path.
/// </summary>
public sealed partial class SparseBlobPoolPeerRegistry
{
    private const int MaxWrappedTransactions = 1024;
    private static readonly TimeSpan WrappedTransactionLifetime = MaxTrackedStateLifetime;

    private readonly ConcurrentDictionary<ValueHash256, WrappedBlobTx> _wrapped = new();

    internal int WrappedCount => _wrapped.Count;

    internal bool IsAwaitingCells(Hash256 hash) => _wrapped.ContainsKey(hash.ValueHash256);

    /// <inheritdoc/>
    public AcceptTxResult Admit(Transaction transaction, object? source, IDisposable proofPin)
    {
        Hash256? hash = transaction.Hash;
        if (Volatile.Read(ref _disposed) != 0 || hash is null || !transaction.SupportsFrames
            || transaction.NetworkWrapper is not ShardBlobNetworkWrapper { Version: ProofVersion.V1, Blobs.Length: 0 } wrapper)
        {
            proofPin.Dispose();
            return AcceptTxResult.Invalid.WithMessage("Not a blob-carrying frame transaction in its elided network form.");
        }

        if (_wrapped.Count >= MaxWrappedTransactions)
        {
            proofPin.Dispose();
            return AcceptTxResult.IncompleteBlobData.WithMessage("Too many blob transactions await cell retrieval; retry later.");
        }

        AcceptTxResult valid = _txPool.ValidateTxForBlobSampling(transaction);
        if (!valid)
        {
            proofPin.Dispose();
            return valid;
        }

        WrappedBlobTx entry = new(transaction, wrapper, proofPin, _timestamper.UtcNowOffset + WrappedTransactionLifetime);
        if (!_wrapped.TryAdd(hash.ValueHash256, entry))
        {
            // Already awaiting its cells under an earlier wrapper.
            proofPin.Dispose();
            return AcceptTxResult.Accepted;
        }

        RecordWrapped(hash, entry, source as ISparseBlobPoolPeer);
        return AcceptTxResult.Accepted;
    }

    /// <summary>Records the wrapped body for cell admission, attributed to <paramref name="preferred"/> when it is an active
    /// peer, else to any active peer that announced the cells; without one it waits for the first cell announcement.</summary>
    private void RecordWrapped(Hash256 hash, WrappedBlobTx entry, ISparseBlobPoolPeer? preferred)
    {
        ISparseBlobPoolPeer? peer = preferred is not null && IsActivePeer(preferred) ? preferred : null;
        if (peer is null && _transactions.TryGetValue(hash.ValueHash256, out TrackedSparseBlobTx? state))
        {
            lock (state.Lock)
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
            RecordTransaction(peer, entry.CreateBody());
        }
    }

    /// <summary>Hands the wrapped body back to admission once a peer announces its cells.</summary>
    private void OnWrappedAnnouncement(ISparseBlobPoolPeer peer, Hash256 hash)
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

    /// <summary>Whether cells may be requested: a cell announcement of a frame transaction waits for its validated wrapper.</summary>
    private bool MayRequestCells(Hash256 hash, TrackedSparseBlobTx state)
        => !state.RequiresWrapper
            || _wrapped.ContainsKey(hash.ValueHash256)
            || _txPool.TryGetPendingBlobCellMask(hash, out _);

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
            entry.ProofPin.Dispose();
        }
    }

    private void MaintainWrapped(DateTimeOffset now)
    {
        foreach (KeyValuePair<ValueHash256, WrappedBlobTx> entry in _wrapped)
        {
            if (now >= entry.Value.ExpiresAt || _txPool.TryGetPendingBlobTransaction(entry.Key.ToHash256(), out _))
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

    /// <summary>A body from a validated wrapper, its pinned proof coverage and the deadline for its cells.</summary>
    private sealed class WrappedBlobTx(Transaction body, ShardBlobNetworkWrapper wrapper, IDisposable proofPin, DateTimeOffset expiresAt)
    {
        public IDisposable ProofPin { get; } = proofPin;
        public DateTimeOffset ExpiresAt { get; } = expiresAt;

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
}
