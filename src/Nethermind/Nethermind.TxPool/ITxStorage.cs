// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
    IEnumerable<LightTransaction> GetAll();
    void Add(Transaction transaction);
    void Delete(in ValueHash256 hash, in UInt256 timestamp);
}

internal interface IBatchDeleteTxStorage
{
    /// <summary>
    /// Removes the timestamped full-body record and the hash-keyed light and elided records for each key.
    /// </summary>
    void DeleteMany(scoped ReadOnlySpan<TxLookupKey> keys);

    /// <summary>
    /// Atomically writes <paramref name="transaction"/> and removes obsolete full bodies for the same hash.
    /// </summary>
    void Replace(Transaction transaction, scoped ReadOnlySpan<UInt256> obsoleteTimestamps);
}

internal interface ISpecChangeValidationStorage
{
    string? GetSpecChangeValidationMarker();
    void SetSpecChangeValidationMarker(string? marker);
}
