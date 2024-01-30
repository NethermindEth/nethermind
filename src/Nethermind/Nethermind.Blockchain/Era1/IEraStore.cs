// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;

namespace Nethermind.Blockchain;

public interface IEraStore
{
    Task<Block?> FindBlock(long number, CancellationToken cancellation = default);
    Task<(Block?, TxReceipt[]?)> FindBlockAndReceipts(long number, CancellationToken cancellation = default);
}
