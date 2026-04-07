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
/// Decorator that owns the Autofac lifetime scope and disposes it when the env is disposed.
/// Returned by <see cref="IBlockProducerEnvFactory.Create"/> when lifetime is <see cref="BlockProducerEnvLifetime.Transient"/>.
/// </summary>
internal sealed class ScopedBlockProducerEnv(IBlockProducerEnv inner, IAsyncDisposable scope) : IBlockProducerEnv, IAsyncDisposable
{
    public IBlockTree BlockTree => inner.BlockTree;
    public IBlockchainProcessor ChainProcessor => inner.ChainProcessor;
    public IWorldState ReadOnlyStateProvider => inner.ReadOnlyStateProvider;
    public ITxSource TxSource => inner.TxSource;
    public ValueTask DisposeAsync() => scope.DisposeAsync();
}
