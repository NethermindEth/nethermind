// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.TxPool;

/// <summary>
/// EIP-8437 availability for a blob-carrying dependency-bearing transaction: its validated proof wrapper carries it with
/// blob data elided, and it enters the blob pool only once EIP-8070 cell retrieval has passed the usual checks.
/// </summary>
public interface IWrappedBlobTransactionAdmission
{
    /// <summary>Holds a transaction from a validated wrapper until its cells are retrieved and checked, then submits it.</summary>
    /// <param name="transaction">The transaction in its elided network form: commitments and cell proofs, no blob data.</param>
    /// <param name="source">The connection the wrapper came from, if any.</param>
    /// <param name="proofPin">Keeps the transaction's verified dependency coverage until the pool admits or drops it; ownership
    /// passes to the callee, which disposes it in every case.</param>
    /// <returns><see cref="AcceptTxResult.Accepted"/> when the transaction awaits its cells; otherwise why it was refused.</returns>
    AcceptTxResult Admit(Transaction transaction, object? source, IDisposable proofPin);
}
