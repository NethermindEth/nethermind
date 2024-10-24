// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Era1;

/// <summary>
/// An IEraStore is meant to support reading arbitrary block from a directory
/// </summary>
public interface IEraStore: IDisposable
{
    Task<Block?> FindBlock(long number, CancellationToken cancellation = default);
    Task<(Block?, TxReceipt[]?)> FindBlockAndReceipts(long number, CancellationToken cancellation = default);
}
