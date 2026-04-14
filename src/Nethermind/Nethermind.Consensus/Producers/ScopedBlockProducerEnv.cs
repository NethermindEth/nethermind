// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Transactions;
using Nethermind.Evm.State;

namespace Nethermind.Consensus.Producers;

/// <summary>
/// Block producer environment that owns its Autofac lifetime scope.
/// Returned by <see cref="IBlockProducerEnvFactory.CreateTransient"/>. Must be disposed by the caller.
/// </summary>
public sealed class ScopedBlockProducerEnv(IBlockProducerEnv inner, IAsyncDisposable scope) : IBlockProducerEnv, IAsyncDisposable
{
    public IBlockTree BlockTree => inner.BlockTree;
    public IBlockchainProcessor ChainProcessor => inner.ChainProcessor;
    public IWorldState ReadOnlyStateProvider => inner.ReadOnlyStateProvider;
    public ITxSource TxSource => inner.TxSource;
    public ValueTask DisposeAsync() => scope.DisposeAsync();
}
