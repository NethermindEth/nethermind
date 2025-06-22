// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.TxPool
{
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
        /// from a specific sender, sorted by nonce and later tx pool sorting
        /// </summary>
        /// <returns></returns>
        Transaction[] GetPendingTransactionsBySender(Address address);
        void AddPeer(ITxPoolPeer peer);
        void RemovePeer(PublicKey nodeId);
        bool ContainsTx(Hash256 hash, TxType txType);
        AcceptTxResult SubmitTx(Transaction tx, TxHandlingOptions handlingOptions);
        bool RemoveTransaction(Hash256? hash);
        Transaction? GetBestTx();
        IEnumerable<Transaction> GetBestTxOfEachSender();
        bool IsKnown(Hash256 hash);
        bool TryGetPendingTransaction(Hash256 hash, [NotNullWhen(true)] out Transaction? transaction);
        bool TryGetPendingBlobTransaction(Hash256 hash, [NotNullWhen(true)] out Transaction? blobTransaction);
        bool TryGetBlobAndProofV0(byte[] blobVersionedHash,
            [NotNullWhen(true)] out byte[]? blob,
            [NotNullWhen(true)] out byte[]? proof);
        bool TryGetBlobAndProofV1(byte[] blobVersionedHash,
            [NotNullWhen(true)] out byte[]? blob,
            [NotNullWhen(true)] out byte[][]? cellProofs);
        int GetBlobCounts(byte[][] blobVersionedHashes);
        UInt256 GetLatestPendingNonce(Address address);
        event EventHandler<TxEventArgs> NewDiscovered;
        event EventHandler<TxEventArgs> NewPending;
        event EventHandler<TxEventArgs> RemovedPending;
        event EventHandler<TxEventArgs> EvictedPending;
        public bool AcceptTxWhenNotSynced { get; set; }
        bool SupportsBlobs { get; }
    }
}
