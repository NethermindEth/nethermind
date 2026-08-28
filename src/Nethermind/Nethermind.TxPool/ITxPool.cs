// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Network.Contract.Messages;

namespace Nethermind.TxPool
{
    /// <summary>Describes the outcome of merging verified blob cells into a pending transaction.</summary>
    public enum BlobCellMergeResult
    {
        /// <summary>The cells were accepted or were already present.</summary>
        Accepted,

        /// <summary>The target transaction is no longer available for merging.</summary>
        TransactionUnavailable,

        /// <summary>The supplied cells or proofs are invalid for the target transaction.</summary>
        InvalidCells,
    }

    public interface ITxPool
    {
        int GetPendingTransactionsCount();
        int GetPendingBlobTransactionsCount();
        Transaction[] GetPendingTransactions();

        public event EventHandler<Block>? TxPoolHeadChanged;

        /// <summary>
        /// Non-blob txs grouped by sender address, sorted by nonce and later tx pool sorting
        /// </summary>
        /// <returns></returns>
        IDictionary<AddressAsKey, Transaction[]> GetPendingTransactionsBySender(bool filterToReadyTx = false, UInt256 baseFee = default);

        /// <summary>
        /// Blob txs light equivalences grouped by sender address, sorted by nonce and later tx pool sorting
        /// </summary>
        /// <returns></returns>
        IDictionary<AddressAsKey, Transaction[]> GetPendingLightBlobTransactionsBySender();

        /// <summary>
        /// Blob txs light equivalences grouped by sender, optionally limited to executable sender heads.
        /// </summary>
        IDictionary<AddressAsKey, Transaction[]> GetPendingLightBlobTransactionsBySender(
            bool filterToReadyTx,
            UInt256 baseFee = default) => GetPendingLightBlobTransactionsBySender();

        /// <summary>
        /// from a specific sender, sorted by nonce and later tx pool sorting
        /// </summary>
        /// <returns></returns>
        Transaction[] GetPendingTransactionsBySender(Address address);

        /// <summary>
        /// Blob txs light equivalences from a specific sender, sorted by nonce.
        /// </summary>
        Transaction[] GetPendingLightBlobTransactionsBySender(Address address) =>
            GetPendingLightBlobTransactionsBySender().TryGetValue(address, out Transaction[]? txs) ? txs : [];
        void AddPeer(ITxPoolPeer peer);
        void RemovePeer(PublicKey nodeId);
        bool ContainsTx(Hash256 hash, TxType txType);
        AnnounceResult NotifyAboutTx(Hash256 txhash, IMessageHandler<PooledTransactionRequestMessage> retryHandler);
        AcceptTxResult SubmitTx(Transaction tx, TxHandlingOptions handlingOptions);
        /// <summary>
        /// Validates a sparse blob transaction before sampler cell retrieval without inserting it
        /// or performing KZG cell-proof verification.
        /// </summary>
        AcceptTxResult ValidateTxForBlobSampling(Transaction tx);
        /// <summary>
        /// Allows a rejected sparse blob transaction to be retried with a different sidecar proof tuple.
        /// </summary>
        void ForgetRejectedBlobTransaction(Hash256 hash);
        bool RemoveTransaction(Hash256? hash);
        bool EvictTransaction(Transaction tx);
        Transaction? GetBestTx();
        IEnumerable<Transaction> GetBestTxOfEachSender();
        bool IsKnown(Hash256 hash);
        bool TryGetPendingTransaction(Hash256 hash, [NotNullWhen(true)] out Transaction? transaction);

        /// <summary>
        /// Gets a pending transaction for metadata-only consumers. Blob and cell payloads are
        /// elided from returned blob transactions while commitments and proofs are preserved.
        /// </summary>
        bool TryGetPendingTransactionWithoutBlobs(Hash256 hash, [NotNullWhen(true)] out Transaction? transaction)
        {
            if (TryGetPendingTransaction(hash, out transaction) && transaction is not null)
            {
                transaction = BlobTransactionPayload.Elide(transaction);
                return true;
            }

            transaction = default;
            return false;
        }
        bool TryGetPendingBlobTransaction(Hash256 hash, [NotNullWhen(true)] out Transaction? blobTransaction);
        bool TryGetBlobAndProofV0(byte[] blobVersionedHash,
            [NotNullWhen(true)] out byte[]? blob,
            [NotNullWhen(true)] out byte[]? proof);
        bool TryGetBlobAndProofV1(byte[] blobVersionedHash,
            [NotNullWhen(true)] out byte[]? blob,
            [NotNullWhen(true)] out byte[][]? cellProofs);
        int TryGetBlobsAndProofsV1(byte[][] requestedBlobVersionedHashes,
            Span<byte[]?> blobs, Span<ReadOnlyMemory<byte[]>> proofs);
        /// <summary>
        /// Gets the cell availability mask of a pending blob transaction without materializing blobs or cells.
        /// </summary>
        /// <returns><c>true</c> when the transaction is present in the blob pool.</returns>
        bool TryGetPendingBlobCellMask(Hash256 hash, out BlobCellMask availableMask);

        /// <summary>
        /// Gets blob-cell serving metadata without materializing blob payloads or touching persistent storage.
        /// </summary>
        /// <param name="materializationWork">Cell-equivalent work needed to load or derive the stored cells.</param>
        bool TryGetPendingBlobCellMetadata(
            Hash256 hash,
            out BlobCellMask availableMask,
            out int blobCount,
            out int materializationWork);

        /// <summary>
        /// Gets locally available cells for a pending blob transaction.
        /// </summary>
        /// <returns><c>true</c> when the transaction exists and at least one requested cell is available.</returns>
        bool TryGetBlobCells(
            Hash256 hash,
            BlobCellMask requestedMask,
            out BlobCellMask availableMask,
            [NotNullWhen(true)] out byte[][]? cells);

        /// <summary>
        /// Gets locally available cells and V1 proofs for a blob versioned hash.
        /// </summary>
        /// <returns><c>true</c> when the blob hash is known, including when none of the requested cells are available.</returns>
        bool TryGetBlobCellsAndProofsV1(
            byte[] blobVersionedHash,
            BlobCellMask requestedMask,
            out BlobCellMask availableMask,
            [NotNullWhen(true)] out byte[][]? cells,
            [NotNullWhen(true)] out byte[][]? proofs);

        /// <summary>
        /// Verifies and merges newly received cells into a pending sparse blob transaction.
        /// </summary>
        bool TryMergeBlobCells(Hash256 hash, BlobCellMask cellMask, byte[][] cells);

        /// <summary>
        /// Verifies and merges newly received cells and reports why the merge was rejected.
        /// </summary>
        /// <returns>The outcome of the merge attempt.</returns>
        BlobCellMergeResult MergeBlobCells(Hash256 hash, BlobCellMask cellMask, byte[][] cells);
        ulong GetLatestPendingNonce(Address address);
        event EventHandler<TxEventArgs> NewDiscovered;
        event EventHandler<TxEventArgs> NewPending;
        event EventHandler<TxEventArgs> RemovedPending;
        event EventHandler<TxEventArgs> EvictedPending;
        public bool AcceptTxWhenNotSynced { get; set; }
        bool SupportsBlobs { get; }
        long PendingTransactionsAdded { get; }

        /// <summary>
        /// Resets txpool state by clearing all caches (hash cache, account cache) 
        /// and removing all pending transactions. Used for integration testing after chain reorgs.
        /// </summary>
        void ResetTxPoolState();
    }
}
