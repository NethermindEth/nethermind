// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.TxPool
{
    /// <summary>
    /// Read-only lookup of an already-pooled transaction by hash. Segregated from the full
    /// <see cref="ITxPool"/> so consumers that only need to reuse a pending transaction
    /// (e.g. to skip re-recovering its sender) do not take a dependency on the whole pool.
    /// </summary>
    public interface IPendingTxLookup
    {
        bool TryGetPendingTransaction(Hash256 hash, [NotNullWhen(true)] out Transaction? transaction);
    }
}
