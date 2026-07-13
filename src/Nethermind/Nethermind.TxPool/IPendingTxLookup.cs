// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.TxPool
{
    /// <summary>Read-only lookup of a pooled transaction by hash, segregated from <see cref="ITxPool"/>.</summary>
    public interface IPendingTxLookup
    {
        bool TryGetPendingTransaction(Hash256 hash, [NotNullWhen(true)] out Transaction? transaction);
    }
}
