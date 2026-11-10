// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Serialization.Rlp;

[Flags]
public enum RlpBehaviors
{
    None,
    AllowExtraBytes = 1,
    ForSealing = 2,
    Storage = 4,
    Eip658Receipts = 8,
    AllowUnsigned = 16,

    /// <summary>
    /// Introduced after typed transactions. In the network (devp2p) transaction has additional wrapping
    /// when we're calculating tx hash or sending raw transaction we should skip this wrapping
    /// with additional wrapping for typed transactions we're decoding Uint8Array([TransactionType, TransactionPayload]
    /// without wrapping we're decoding (TransactionType || TransactionPayload)
    /// </summary>
    SkipTypedWrapping = 32,

    /// <summary>
    /// Transactions like Shard Blob ones can be passed in mempool or execution payload version form
    /// Execution payload form: TX_TYPE || [chain_id, nonce, ...];
    /// Mempool form: TX_TYPE || [[chain_id, nonce, ...], &lt;mempool wrapper fields&gt; ].
    /// See https://eips.ethereum.org/EIPS/eip-4844#networking
    /// </summary>
    InMempoolForm = 64,
    ExcludeHashes = 128,

    /// <summary>
    /// Pools blob buffers for exclusively owned incoming P2P transactions. Their payload may be
    /// released on rejection; callers must relinquish pooling ownership before sharing the transaction.
    /// </summary>
    PoolBlobBuffers = 256,

    /// <summary>
    /// Creates fresh transaction objects for long-lived payloads instead of renting them from the object pool.
    /// </summary>
    SkipPooledTransactions = 512,

    /// <summary>
    /// EIP-7668: encodes the receipt bloom as a zero-length string, whatever bloom the receipt holds;
    /// the receipt message decoder accepts a zero-length bloom and the compact storage decoder sets
    /// <c>Bloom.ZeroLength</c> instead of computing the bloom.
    /// </summary>
    Eip7668Receipts = 1024,

    /// <summary>
    /// EIP-1559: encodes the header base fee even when it is zero, as required from London. Without it a zero base fee
    /// is encoded only when a later field follows, which gives a London header before Shanghai the pre-London shape.
    /// </summary>
    Eip1559Header = 2048,

    /// <summary>
    /// The input is canonical RLP that this node encoded and stored, or that was checked against a consensus hash
    /// before it was stored. The decode skips the checks of the canonical form, but keeps every bounds check, so that
    /// corrupted input still throws.
    /// </summary>
    /// <remarks>Never set it for input from the network, from RPC or from the Engine API.</remarks>
    TrustedInput = 4096
}
