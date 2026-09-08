// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Era1;

/// <summary>
/// An IEraStore is the main high level block reader.
/// It is meant to be high level, allowing direct read of block/receipt from era directory addressed by block number.
///
/// Internally it uses EraReader, but that should be considered implementation details.
/// </summary>
public interface IEraStore : IDisposable
{
    Task<(Block?, TxReceipt[]?)> FindBlockAndReceipts(ulong number, bool ensureValidated = true, CancellationToken cancellation = default);
    ulong LastBlock { get; }
    ulong FirstBlock { get; }

    /// Used for optimization where multiple tasks should not read on the same era file.
    /// Ideally not necessary in the future.
    ulong NextEraStart(ulong blockNumber);
}
