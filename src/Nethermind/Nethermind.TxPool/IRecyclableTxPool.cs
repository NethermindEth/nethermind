// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.TxPool;

/// <summary>
/// Submits exclusively owned incoming transactions and reports whether rejection left ownership with the caller.
/// </summary>
internal interface IRecyclableTxPool
{
    /// <remarks>
    /// A true <paramref name="canRecycle"/> means no transaction reference escaped submission.
    /// The caller must finish reading the transaction before returning it to its object pool.
    /// </remarks>
    AcceptTxResult SubmitOwnedTx(Transaction tx, out bool canRecycle);
}
