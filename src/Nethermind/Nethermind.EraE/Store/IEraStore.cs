// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.EraE.Store;

public interface IEraStore : IDisposable
{
    Task<(Block?, TxReceipt[]?)> FindBlockAndReceipts(ulong number, bool ensureValidated = true, CancellationToken cancellation = default);

    /// <summary>Finds a block whose transactions keep only their encoding, its encoded body, and its receipts.</summary>
    /// <remarks>
    /// The transactions are pooled and refer to the encoded body, so return them to <c>TxDecoder.TxObjectPool</c>
    /// when done. Use it for blocks that are stored but not executed.
    /// </remarks>
    Task<(Block?, byte[]?, TxReceipt[]?)> FindEncodedBlockAndReceipts(ulong number, bool ensureValidated = true, CancellationToken cancellation = default);
    (ulong First, ulong Last) BlockRange { get; }

    bool HasEpoch(ulong blockNumber);

    /// Used for alignment when parallelizing imports so different tasks work on different files.
    ulong NextEraStart(ulong blockNumber);
}
