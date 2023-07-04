// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
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
    All = AllowExtraBytes | ForSealing | Storage | Eip658Receipts | AllowUnsigned | SkipTypedWrapping | InMempoolForm
}
