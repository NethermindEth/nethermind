// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Consensus.Processing;

namespace Nethermind.Core.Test.Modules;

/// <summary>
/// A small container that wraps components tha have the same lifetime as the main IWorldState.
/// </summary>
/// <param name="LifetimeScope"></param>
/// <param name="BlockchainProcessor"></param>
/// <param name="BlockProcessor"></param>
/// <param name="GenesisLoader"></param>
public record MainBlockProcessingContext(
    ILifetimeScope LifetimeScope,
    BlockchainProcessor BlockchainProcessor,
    IBlockProcessor BlockProcessor,
    GenesisLoader GenesisLoader) : IAsyncDisposable
{
    public IBlockProcessingQueue BlockProcessingQueue => BlockchainProcessor;

    public async ValueTask DisposeAsync()
    {
        await LifetimeScope.DisposeAsync();
    }
}