// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Core
{
    public partial class Transaction
    {
        /// <inheritdoc cref="CalculateHashSynchronized"/>
        /// <remarks>The guest is single-threaded, so there is no race to guard and the lock is pure
        /// overhead - 1,231 acquisitions over one mainnet block.</remarks>
        private partial Hash256 CalculateHashSynchronized() => ComputeAndMemoizeHash();
    }
}
