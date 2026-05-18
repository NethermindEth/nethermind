// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Facade.Eth;
using Nethermind.Synchronization;
using Nethermind.Synchronization.ParallelSync;

namespace Nethermind.Taiko;

/// <remarks>
/// Wires Taiko-specific sync decorators:
/// <see cref="ZeroTotalDifficultyStrategy"/> (Taiko's <c>TotalDifficulty</c> is always 0;
/// the default cumulative strategy underflows on the per-block ZK-gas <c>Difficulty</c>),
/// <see cref="TaikoSyncProgressResolver"/> (widens
/// <see cref="ISyncProgressResolver.FindBestHeader"/> for the snapshot-invariant check),
/// and <see cref="TaikoEthSyncingInfo"/> (widens the suggested-header read for
/// <c>eth_syncing</c>).
/// </remarks>
public sealed class TaikoSynchronizerModule : Module
{
    protected override void Load(ContainerBuilder builder) => builder
        .AddSingleton<ITotalDifficultyStrategy, ZeroTotalDifficultyStrategy>()
        .AddDecorator<ISyncProgressResolver>((ctx, inner) =>
            new TaikoSyncProgressResolver(ctx.Resolve<IBlockTree>(), inner))
        .AddDecorator<IEthSyncingInfo>((ctx, inner) =>
            new TaikoEthSyncingInfo(ctx.Resolve<IBlockTree>(), inner))
        .AddSingleton<TaikoBeaconHeadAdvancer>();
}
