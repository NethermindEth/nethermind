// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.TxPool;

/// <summary>
/// Optional blob storage capability for reading and persisting sidecar-free transactions.
/// </summary>
public interface IBlobTxMetadataStorage
{
    /// <summary>
    /// Gets a transaction with blob and cell payloads elided while preserving commitments and proofs.
    /// </summary>
    bool TryGetWithoutBlobs(in ValueHash256 hash, Address sender, [NotNullWhen(true)] out Transaction? transaction);

    /// <summary>
    /// Persists a sidecar-free transaction only while its corresponding full record still exists.
    /// </summary>
    void AddWithoutBlobs(Transaction transaction);
}
