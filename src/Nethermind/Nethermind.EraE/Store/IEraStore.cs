// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.EraE.Store;

public interface IEraStore : IDisposable
{
    Task<(Block?, TxReceipt[]?)> FindBlockAndReceipts(long number, bool ensureValidated = true, CancellationToken cancellation = default);
    long LastBlock { get; }
    long FirstBlock { get; }

    bool HasEpoch(long blockNumber);

    /// Used for alignment when parallelizing imports so different tasks work on different files.
    long NextEraStart(long blockNumber);
}
