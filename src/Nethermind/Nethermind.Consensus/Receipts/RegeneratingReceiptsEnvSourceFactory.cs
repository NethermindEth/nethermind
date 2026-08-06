// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Autofac;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.State.OverridableEnv;

namespace Nethermind.Consensus.Receipts;

/// <summary>The read-only block-processing components <see cref="ReceiptsRegenerator"/> re-executes a block with.</summary>
public sealed record ReceiptsRegenerationEnv(IBlockProcessor BlockProcessor);

/// <summary>
/// Builds the pooled supply of read-only execution environments that <see cref="ReceiptsRegenerator"/> re-executes
/// historical blocks in.
/// </summary>
/// <remarks>
/// Each pooled environment owns an independent overridable world state and block processor, so concurrent receipt
/// queries never share mutable execution state. The scope mirrors the read-only re-execution the Flashbots block
/// validator uses to recompute a block's receipts: the standard block-validation wiring over an overridable world
/// state, with receipt persistence nulled so nothing is written back.
/// </remarks>
public sealed class RegeneratingReceiptsEnvSourceFactory(
    IOverridableEnvFactory envFactory,
    ILifetimeScope rootLifetimeScope,
    IBlockValidationModule[] validationModules)
{
    public IShareableOverridableEnvSource<ReceiptsRegenerationEnv> Create(int maxConcurrent) =>
        new ShareableOverridableEnvSource<ReceiptsRegenerationEnv>(BuildSingleEnv, maxConcurrent);

    // One env per invocation — independent world scope and processing chain so pooled renters share no mutable state.
    private IOverridableEnv<ReceiptsRegenerationEnv> BuildSingleEnv()
    {
        IOverridableEnv env = envFactory.Create();
        ILifetimeScope envScope = rootLifetimeScope.BeginLifetimeScope((builder) => builder
            .AddModule(validationModules)
            .AddSingleton<IReceiptStorage>(NullReceiptStorage.Instance)
            .AddScoped<ReceiptsRegenerationEnv>()
            .AddModule(env));

        // The pool owns the scope; registering it with rootLifetimeScope.Disposer would retain every created env
        // until node shutdown and turn burst query traffic into long-lived memory.
        return new DisposableOverridableEnv(envScope.Resolve<IOverridableEnv<ReceiptsRegenerationEnv>>(), envScope);
    }

    private sealed class DisposableOverridableEnv(
        IOverridableEnv<ReceiptsRegenerationEnv> inner,
        IDisposable scope) : IOverridableEnv<ReceiptsRegenerationEnv>, IDisposable
    {
        public Scope<ReceiptsRegenerationEnv> BuildAndOverride(
            BlockHeader? header,
            Dictionary<Address, AccountOverride>? stateOverride = null,
            IReleaseSpec? specOverride = null,
            BlockOverride? blockOverride = null,
            bool requireStateRoot = true) =>
            inner.BuildAndOverride(header, stateOverride, specOverride, blockOverride, requireStateRoot);

        public void Dispose() => scope.Dispose();
    }
}
