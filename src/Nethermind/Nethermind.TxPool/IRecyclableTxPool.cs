// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

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
    /// <param name="peer">The sending peer, compared by reference, whose share of the per-head frame validation
    /// time pays for a frame transaction rejected after validation; <see langword="null"/> exempts it.</param>
    AcceptTxResult SubmitOwnedTx(Transaction tx, object? peer, out bool canRecycle);

    /// <summary>Settles an incoming transaction the pool already knows, given only its hash.</summary>
    /// <returns>
    /// Whether the transaction was settled as <see cref="AcceptTxResult.AlreadyKnown"/>; when false, the caller
    /// decodes it and submits it instead.
    /// </returns>
    /// <remarks>
    /// Has the side effects <see cref="SubmitOwnedTx"/> would have for a known transaction, so a duplicate need
    /// not be decoded at all.
    /// </remarks>
    bool TrySubmitKnown(in ValueHash256 hash);
}
