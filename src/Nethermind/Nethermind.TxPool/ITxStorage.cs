// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.TxPool;

public readonly record struct TxLookupKey(ValueHash256 Hash, Address Sender, UInt256 Timestamp);

public interface ITxStorage
{
    bool TryGet(in ValueHash256 hash, Address sender, in UInt256 timestamp, [NotNullWhen(true)] out Transaction? transaction);
    int TryGetMany(TxLookupKey[] keys, int count, Transaction?[] results);

    /// <summary>
    /// Gets a transaction with blob payloads elided: the network wrapper keeps commitments and
    /// proofs, but blobs and cells are empty.
    /// </summary>
    /// <remarks>
    /// Reads a small sidecar-free record instead of the full transaction row, so metadata-only
    /// consumers (eth/72 pooled transaction serving) do not materialize blob payloads.
    /// Records persisted before the sidecar-free record existed fall back to a full read once and
    /// are upgraded on the fly.
    /// </remarks>
    bool TryGetWithoutBlobs(in ValueHash256 hash, Address sender, in UInt256 timestamp, [NotNullWhen(true)] out Transaction? transaction);

    IEnumerable<LightTransaction> GetAll();
    void Add(Transaction transaction);
    void Delete(in ValueHash256 hash, in UInt256 timestamp);
}
