// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Network.Contract.Messages;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Nethermind.TxPool
{
    public class NullTxPool : ITxPool
    {
        public bool SupportsBlobs => false;
        private NullTxPool() { }

        public static NullTxPool Instance { get; } = new();

        public event EventHandler<Block>? TxPoolHeadChanged
        {
            add { }
            remove { }
        }

        public int GetPendingTransactionsCount() => 0;
        public int GetPendingBlobTransactionsCount() => 0;
        public long PendingTransactionsAdded => 0;
        public Transaction[] GetPendingTransactions() => [];

        public Transaction[] GetPendingTransactionsBySender(Address address) => [];

        public IDictionary<AddressAsKey, Transaction[]> GetPendingTransactionsBySender(bool filterToReadyTx = false, UInt256 baseFee = default)
            => new Dictionary<AddressAsKey, Transaction[]>();

        public IDictionary<AddressAsKey, Transaction[]> GetPendingLightBlobTransactionsBySender()
            => new Dictionary<AddressAsKey, Transaction[]>();

        public IDictionary<AddressAsKey, Transaction[]> GetPendingLightBlobTransactionsBySender(bool filterToReadyTx, UInt256 baseFee = default)
            => new Dictionary<AddressAsKey, Transaction[]>();

        public Transaction[] GetPendingLightBlobTransactionsBySender(Address address) => [];

        public void AddPeer(ITxPoolPeer peer) { }

        public void RemovePeer(PublicKey nodeId) { }

        public bool ContainsTx(Hash256 hash, TxType txType) => false;

        public AcceptTxResult SubmitTx(Transaction tx, TxHandlingOptions txHandlingOptions) => AcceptTxResult.Accepted;

        public void ForgetRejectedBlobTransaction(Hash256 hash) { }

        public bool RemoveTransaction(Hash256? hash) => false;

        public Transaction? GetBestTx() => null;

        public IEnumerable<Transaction> GetBestTxOfEachSender() => Array.Empty<Transaction>();

        public bool IsKnown(Hash256 hash) => false;

        public bool TryGetPendingTransaction(Hash256 hash, [NotNullWhen(true)] out Transaction? transaction)
        {
            transaction = null;
            return false;
        }

        public bool TryGetPendingBlobTransaction(Hash256 hash, [NotNullWhen(true)] out Transaction? blobTransaction)
        {
            blobTransaction = null;
            return false;
        }

        public bool TryGetBlobAndProofV0(byte[] blobVersionedHash,
            [NotNullWhen(true)] out byte[]? blob,
            [NotNullWhen(true)] out byte[]? proof)
        {
            blob = null;
            proof = null;
            return false;
        }

        public bool TryGetBlobAndProofV1(byte[] blobVersionedHash,
            [NotNullWhen(true)] out byte[]? blob,
            [NotNullWhen(true)] out byte[][]? cellProofs)
        {
            blob = null;
            cellProofs = null;
            return false;
        }

        public int TryGetBlobsAndProofsV1(byte[][] requestedBlobVersionedHashes,
            Span<byte[]?> blobs, Span<ReadOnlyMemory<byte[]>> proofs) => 0;

        public bool TryGetPendingBlobCellMask(Hash256 hash, out BlobCellMask availableMask)
        {
            availableMask = default;
            return false;
        }

        public bool TryGetBlobCells(Hash256 hash, BlobCellMask requestedMask, out BlobCellMask availableMask, [NotNullWhen(true)] out byte[][]? cells)
        {
            availableMask = default;
            cells = null;
            return false;
        }

        public bool TryGetBlobCellsAndProofsV1(byte[] blobVersionedHash, BlobCellMask requestedMask, out BlobCellMask availableMask, [NotNullWhen(true)] out byte[][]? cells, [NotNullWhen(true)] out byte[][]? proofs)
        {
            availableMask = default;
            cells = null;
            proofs = null;
            return false;
        }

        public bool TryMergeBlobCells(Hash256 hash, BlobCellMask cellMask, byte[][] cells) => false;

        public ulong GetLatestPendingNonce(Address address) => 0;

        public AnnounceResult NotifyAboutTx(Hash256 txhash, IMessageHandler<PooledTransactionRequestMessage> retryHandler) => AnnounceResult.RequestRequired;

        public event EventHandler<TxEventArgs> NewDiscovered
        {
            add { }
            remove { }
        }

        public event EventHandler<TxEventArgs> NewPending
        {
            add { }
            remove { }
        }

        public event EventHandler<TxEventArgs> RemovedPending
        {
            add { }
            remove { }
        }

        public event EventHandler<TxEventArgs>? EvictedPending
        {
            add { }
            remove { }
        }
        public bool AcceptTxWhenNotSynced { get; set; }
        public void ResetTxPoolState() { }
    }
}
