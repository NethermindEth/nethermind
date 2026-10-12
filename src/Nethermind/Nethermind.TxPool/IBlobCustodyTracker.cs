// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.TxPool;

/// <summary>Tracks the blob-cell columns currently custodied by the local consensus client.</summary>
/// <remarks>
/// Implementations are thread-safe. Concurrent updates may raise <see cref="CustodyChanged"/>
/// callbacks concurrently or in a different order from callback completion.
/// </remarks>
public interface IBlobCustodyTracker
{
    /// <summary>Gets a thread-safe snapshot of the current custody mask.</summary>
    BlobCellMask CurrentMask { get; }

    /// <summary>Raised after the custody mask changes.</summary>
    event EventHandler<BlobCellMask>? CustodyChanged;

    /// <summary>Updates the custody mask.</summary>
    /// <returns><c>true</c> if the mask changed; <c>false</c> if it was already current.</returns>
    bool Update(BlobCellMask mask);
}
