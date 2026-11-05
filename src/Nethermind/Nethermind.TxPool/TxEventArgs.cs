// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.TxPool
{
    /// <remarks>A struct so raising a pool event per transaction allocates nothing.</remarks>
    public readonly struct TxEventArgs(Transaction transaction)
    {
        public Transaction Transaction { get; } = transaction;
    }
}
