// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CkzgLib;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Stats.Model;
using Nethermind.TxPool;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V72;

public sealed class SparseBlobPoolPeerRegistry(
    ITxPool txPool,
    IBackgroundTaskScheduler backgroundTaskScheduler,
    ILogManager logManager)
    : ISparseBlobPoolPeerRegistry
{
    private const int MaxTrackedTransactions = 8192;
    private const int MinIndependentProviderAnnouncements = 2;
    private static readonly TimeSpan SaturationTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxAdmissionDelay = TimeSpan.FromMilliseconds(64);
    private static readonly PublicKey NoPreferredPeer = new(new byte[PublicKey.LengthInBytes]);

    private readonly ITxPool _txPool = txPool ?? throw new ArgumentNullException(nameof(txPool));
    private readonly IBackgroundTaskScheduler _backgroundTaskScheduler = backgroundTaskScheduler ?? throw new ArgumentNullException(nameof(backgroundTaskScheduler));
    private readonly ILogger _logger = (logManager ?? throw new ArgumentNullException(nameof(logManager))).GetClassLogger<SparseBlobPoolPeerRegistry>();
    private readonly ConcurrentDictionary<PublicKey, ISparseBlobPoolPeer> _peers = new();
    private readonly ConcurrentDictionary<ValueHash256, TrackedSparseBlobTx> _transactions = new();
    private readonly ConcurrentQueue<ValueHash256> _transactionOrder = new();

    public void AddPeer(ISparseBlobPoolPeer peer) => _peers[peer.Id] = peer;

    public void RemovePeer(PublicKey peerId) => _peers.TryRemove(peerId, out _);

    public void RecordAnnouncement(ISparseBlobPoolPeer peer, Hash256 hash, BlobCellMask announcementMask)
    {
        if (announcementMask.IsEmpty)
        {
            return;
        }

        TrackedSparseBlobTx state = GetOrAdd(hash);
        lock (state.Lock)
        {
            state.Announcements[peer.Id] = announcementMask;
        }

        ScheduleSaturationCheck(hash);
    }

    public bool TryRequestCells(Hash256 hash, BlobCellMask requestMask, PublicKey preferredPeerId)
    {
        if (requestMask.IsEmpty || !_transactions.TryGetValue(hash.ValueHash256, out TrackedSparseBlobTx? state))
        {
            return false;
        }

        ISparseBlobPoolPeer? peer = SelectPeer(state, requestMask, preferredPeerId);
        if (peer is null)
        {
            return false;
        }

        return peer.TrySendGetCells(hash, requestMask);
    }

    public AcceptTxResult? RecordTransaction(ISparseBlobPoolPeer peer, Transaction transaction)
    {
        Hash256? hash = transaction.Hash;
        if (hash is null || !transaction.SupportsBlobs)
        {
            return SubmitTransaction(peer, transaction);
        }

        if (transaction.NetworkWrapper is ShardBlobNetworkWrapper wrapper && wrapper.HasFullBlobs())
        {
            return SubmitTransaction(peer, transaction);
        }

        TrackedSparseBlobTx state = GetOrAdd(hash);
        lock (state.Lock)
        {
            state.Transaction ??= transaction;
            state.TransactionPeer ??= peer;
        }

        return TrySubmit(hash, state);
    }

    public bool RecordCells(ISparseBlobPoolPeer peer, Hash256 hash, BlobCellMask cellMask, byte[][] cells)
    {
        if (cellMask.IsEmpty || cells.Length == 0)
        {
            return false;
        }

        TrackedSparseBlobTx state = GetOrAdd(hash);
        lock (state.Lock)
        {
            state.Cells = new PendingCellsBuffer(cellMask, cells, peer.Id);
        }

        TrySubmit(hash, state);
        return true;
    }

    public void Clear(Hash256 hash) => _transactions.TryRemove(hash.ValueHash256, out _);

    private TrackedSparseBlobTx GetOrAdd(Hash256 hash)
    {
        ValueHash256 key = hash.ValueHash256;
        if (_transactions.TryGetValue(key, out TrackedSparseBlobTx? existing))
        {
            return existing;
        }

        TrackedSparseBlobTx state = new(DateTimeOffset.UtcNow + GetAdmissionDelay(hash));
        if (_transactions.TryAdd(key, state))
        {
            _transactionOrder.Enqueue(key);
            TrimTrackedTransactions();
            return state;
        }

        return _transactions[key];
    }

    private ISparseBlobPoolPeer? SelectPeer(TrackedSparseBlobTx state, BlobCellMask requestMask, PublicKey preferredPeerId)
    {
        lock (state.Lock)
        {
            if (state.Announcements.Count == 0)
            {
                return null;
            }

            ISparseBlobPoolPeer? preferredPeer = null;
            ISparseBlobPoolPeer? selectedPeer = null;
            int nonPreferredCount = 0;
            foreach (KeyValuePair<PublicKey, BlobCellMask> announcement in state.Announcements)
            {
                if ((announcement.Value & requestMask).IsEmpty
                    || !_peers.TryGetValue(announcement.Key, out ISparseBlobPoolPeer? peer)
                    || peer.IsClosing)
                {
                    continue;
                }

                if (peer.Id == preferredPeerId)
                {
                    preferredPeer ??= peer;
                    continue;
                }

                nonPreferredCount++;
                if (Random.Shared.Next(nonPreferredCount) == 0)
                {
                    selectedPeer = peer;
                }
            }

            // Prefer a random announced provider rather than always leaning on the first signaler.
            return selectedPeer ?? preferredPeer;
        }
    }

    private AcceptTxResult? TrySubmit(Hash256 hash, TrackedSparseBlobTx state)
    {
        if (!_transactions.TryGetValue(hash.ValueHash256, out TrackedSparseBlobTx? current)
            || !ReferenceEquals(current, state))
        {
            return null;
        }

        Transaction? transaction;
        ISparseBlobPoolPeer? transactionPeer;
        PendingCellsBuffer? cells;
        DateTimeOffset notBefore;
        lock (state.Lock)
        {
            transaction = state.Transaction;
            transactionPeer = state.TransactionPeer;
            cells = state.Cells;
            notBefore = state.NotBefore;
            if (transaction is null || cells is null)
            {
                return null;
            }

            if (DateTimeOffset.UtcNow < notBefore)
            {
                ScheduleAdmission(hash, state, notBefore - DateTimeOffset.UtcNow);
                return null;
            }
        }

        if (!TryAttachCells(hash, transaction, cells.Value, out string? error))
        {
            DisconnectPeer(cells.Value.SourcePeerId, DisconnectReason.BreachOfProtocol, error ?? "invalid sparse blob cells");
            lock (state.Lock)
            {
                state.Cells = null;
            }

            return null;
        }

        if (!_transactions.TryRemove(hash.ValueHash256, out TrackedSparseBlobTx? removedState)
            || !ReferenceEquals(removedState, state))
        {
            return null;
        }

        AcceptTxResult result = SubmitTransaction(transactionPeer, transaction);
        if (result == AcceptTxResult.Invalid)
        {
            transactionPeer?.DisconnectSparseBlobPeer(DisconnectReason.InvalidTxReceived, $"Invalid sparse blob transaction {hash}");
        }

        return result;
    }

    private AcceptTxResult SubmitTransaction(ISparseBlobPoolPeer? peer, Transaction transaction)
    {
        transaction.Timestamp = Timestamper.Default.UnixTime.Seconds;
        AcceptTxResult result = _txPool.SubmitTx(transaction, TxHandlingOptions.None);
        if (_logger.IsTrace)
        {
            _logger.Trace($"{peer?.Id} sent sparse blob tx {transaction.Hash} and it was {result}");
        }

        return result;
    }

    private void ScheduleAdmission(Hash256 hash, TrackedSparseBlobTx state, TimeSpan delay)
    {
        lock (state.Lock)
        {
            if (state.AdmissionScheduled)
            {
                return;
            }

            state.AdmissionScheduled = true;
        }

        _backgroundTaskScheduler.TryScheduleTask(
            (Registry: this, Hash: hash, State: state, Delay: delay),
            static async (request, token) =>
            {
                if (request.Delay > TimeSpan.Zero)
                {
                    await Task.Delay(request.Delay, token);
                }

                lock (request.State.Lock)
                {
                    request.State.AdmissionScheduled = false;
                }

                request.Registry.TrySubmit(request.Hash, request.State);
            },
            source: nameof(SparseBlobPoolPeerRegistry));
    }

    private void ScheduleSaturationCheck(Hash256 hash)
    {
        if (!_transactions.TryGetValue(hash.ValueHash256, out TrackedSparseBlobTx? state))
        {
            return;
        }

        lock (state.Lock)
        {
            if (state.SaturationCheckScheduled)
            {
                return;
            }

            state.SaturationCheckScheduled = true;
        }

        _backgroundTaskScheduler.TryScheduleTask(
            (Registry: this, Hash: hash, State: state),
            static async (request, token) =>
            {
                await Task.Delay(SaturationTimeout, token);
                request.Registry.CheckSaturation(request.Hash, request.State);
            },
            source: nameof(SparseBlobPoolPeerRegistry));
    }

    private void CheckSaturation(Hash256 hash, TrackedSparseBlobTx state)
    {
        if (!_transactions.TryGetValue(hash.ValueHash256, out TrackedSparseBlobTx? current)
            || !ReferenceEquals(current, state))
        {
            return;
        }

        int providers = 0;
        bool hasFullProvider = false;
        lock (state.Lock)
        {
            foreach (BlobCellMask mask in state.Announcements.Values)
            {
                if (mask.IsFull)
                {
                    providers++;
                    hasFullProvider = true;
                }
            }

            if (providers >= MinIndependentProviderAnnouncements)
            {
                return;
            }
        }

        if (hasFullProvider && TryRequestCells(hash, BlobCellMask.Full, NoPreferredPeer))
        {
            return;
        }

        if (!_transactions.TryRemove(hash.ValueHash256, out TrackedSparseBlobTx? removedState)
            || !ReferenceEquals(removedState, state))
        {
            return;
        }

        if (_txPool.RemoveTransaction(hash) && _logger.IsDebug)
        {
            _logger.Debug($"Evicted sparse blob transaction {hash} after saturation timeout with {providers} independent provider announcements.");
        }
    }

    private void DisconnectPeer(PublicKey peerId, DisconnectReason reason, string details)
    {
        if (_peers.TryGetValue(peerId, out ISparseBlobPoolPeer? peer))
        {
            peer.DisconnectSparseBlobPeer(reason, details);
        }
    }

    private static bool TryAttachCells(Hash256 hash, Transaction tx, PendingCellsBuffer pending, out string? error)
    {
        error = null;
        if (tx.BlobVersionedHashes is not { Length: > 0 } blobVersionedHashes
            || tx.NetworkWrapper is not ShardBlobNetworkWrapper { Version: ProofVersion.V1 } wrapper
            || pending.CellMask.IsEmpty)
        {
            error = $"Wrong sparse blob transaction form for {hash}.";
            return false;
        }

        int requestedCellsPerBlob = pending.CellMask.Count;
        int blobCount = blobVersionedHashes.Length;
        if (pending.Cells.Length != blobCount * requestedCellsPerBlob)
        {
            error = $"Wrong sparse blob cells count for {hash}.";
            return false;
        }

        BlobCellMask availableMask = BlobCellMask.Empty;
        int availableCount = 0;
        int requestedPosition = 0;
        foreach (int cellIndex in pending.CellMask.EnumerateSetBits())
        {
            bool presentForAllBlobs = true;
            for (int blobIndex = 0; blobIndex < blobCount; blobIndex++)
            {
                byte[] cell = pending.Cells[blobIndex * requestedCellsPerBlob + requestedPosition];
                if (cell.Length is not 0 and not Ckzg.BytesPerCell)
                {
                    error = $"Invalid sparse blob cell size {cell.Length} for {hash}.";
                    return false;
                }

                presentForAllBlobs &= cell.Length == Ckzg.BytesPerCell;
            }

            if (presentForAllBlobs)
            {
                availableMask |= new BlobCellMask(UInt128.One << cellIndex);
                availableCount++;
            }

            requestedPosition++;
        }

        if (availableCount == 0)
        {
            error = $"No requested sparse blob cells available for {hash}.";
            return false;
        }

        byte[][] flattenedCells = new byte[blobCount * availableCount][];
        for (int blobIndex = 0; blobIndex < blobCount; blobIndex++)
        {
            int outputIndex = blobIndex * availableCount;
            int inputIndex = blobIndex * requestedCellsPerBlob;
            int requestIndex = 0;
            foreach (int cellIndex in pending.CellMask.EnumerateSetBits())
            {
                if (availableMask.Contains(cellIndex))
                {
                    flattenedCells[outputIndex++] = pending.Cells[inputIndex + requestIndex];
                }

                requestIndex++;
            }
        }

        ShardBlobNetworkWrapper sparseWrapper = wrapper with { CellMask = availableMask, Cells = flattenedCells };
        if (!BlobCellsHelper.ValidateCells(sparseWrapper))
        {
            error = $"Invalid sparse blob cell proofs for {hash}.";
            return false;
        }

        tx.NetworkWrapper = sparseWrapper;
        return true;
    }

    private void TrimTrackedTransactions()
    {
        while (_transactions.Count > MaxTrackedTransactions && _transactionOrder.TryDequeue(out ValueHash256 hash))
        {
            _transactions.TryRemove(hash, out _);
        }
    }

    private static TimeSpan GetAdmissionDelay(Hash256 hash)
    {
        int maxMilliseconds = (int)MaxAdmissionDelay.TotalMilliseconds;
        if (maxMilliseconds <= 0)
        {
            return TimeSpan.Zero;
        }

        int delay = (hash.Bytes[0] << 8 | hash.Bytes[1]) % (maxMilliseconds + 1);
        return TimeSpan.FromMilliseconds(delay);
    }

    private readonly record struct PendingCellsBuffer(BlobCellMask CellMask, byte[][] Cells, PublicKey SourcePeerId);

    private sealed class TrackedSparseBlobTx(DateTimeOffset notBefore)
    {
        public Lock Lock { get; } = new();
        public DateTimeOffset NotBefore { get; } = notBefore;
        public Dictionary<PublicKey, BlobCellMask> Announcements { get; } = [];
        public Transaction? Transaction { get; set; }
        public ISparseBlobPoolPeer? TransactionPeer { get; set; }
        public PendingCellsBuffer? Cells { get; set; }
        public bool AdmissionScheduled { get; set; }
        public bool SaturationCheckScheduled { get; set; }
    }
}
