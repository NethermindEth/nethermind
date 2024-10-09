// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus;

namespace Nethermind.Shutter;

public interface IShutterApi
{
    ShutterTxSource TxSource { get; }
    Task StartP2P(CancellationTokenSource? cancellationTokenSource = null);
    ShutterBlockImprovementContextFactory GetBlockImprovementContextFactory(IBlockProducer blockProducer);
    ValueTask DisposeAsync();
}
