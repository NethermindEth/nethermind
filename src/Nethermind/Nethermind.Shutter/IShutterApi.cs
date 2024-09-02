// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus;
using Nethermind.Core;

namespace Nethermind.Shutter;

public interface IShutterApi
{
    public ShutterTxSource TxSource { get; }
    void StartP2P(CancellationTokenSource? cancellationTokenSource = null);
    public ShutterBlockImprovementContextFactory GetBlockImprovementContextFactory(IBlockProducer blockProducer);
    public ValueTask DisposeAsync();
}
