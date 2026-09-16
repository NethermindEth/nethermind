// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Core
{
    public partial class Transaction
    {
        /// <inheritdoc cref="CalculateHashSynchronized"/>
        /// <remarks>Two threads can race to hash the same transaction, so the memoization is guarded.</remarks>
        private partial Hash256 CalculateHashSynchronized()
        {
            lock (this)
            {
                return ComputeAndMemoizeHash();
            }
        }
    }
}
