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
    public class NullTxPool : ITxPool
    {
        private NullTxPool() { }

        public static NullTxPool Instance { get; } = new();

        public event EventHandler<Block>? TxPoolHeadChanged
        {
            add { }
            remove { }
        }

        public int GetPendingTransactionsCount() => 0;
        public int GetPendingBlobTransactionsCount() => 0;
        public Transaction[] GetPendingTransactions() => Array.Empty<Transaction>();

        public Transaction[] GetPendingTransactionsBySender(Address address) => Array.Empty<Transaction>();

        public IDictionary<AddressAsKey, Transaction[]> GetPendingTransactionsBySender()
            => new Dictionary<AddressAsKey, Transaction[]>();

        public IDictionary<AddressAsKey, Transaction[]> GetPendingLightBlobTransactionsBySender()
            => new Dictionary<AddressAsKey, Transaction[]>();

        public void AddPeer(ITxPoolPeer peer) { }

        public void RemovePeer(PublicKey nodeId) { }

        public bool ContainsTx(Hash256 hash, TxType txType) => false;

        public AcceptTxResult SubmitTx(Transaction tx, TxHandlingOptions txHandlingOptions) => AcceptTxResult.Accepted;

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

        public bool TryGetBlobAndProof(byte[] blobVersionedHash,
            [NotNullWhen(true)] out byte[]? blob,
            [NotNullWhen(true)] out byte[]? proof)
        {
            blob = null;
            proof = null;
            return false;
        }

        public UInt256 GetLatestPendingNonce(Address address) => 0;


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
    }
}
