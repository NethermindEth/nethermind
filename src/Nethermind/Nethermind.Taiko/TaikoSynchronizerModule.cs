// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Synchronization;
using Nethermind.Synchronization.ParallelSync;

namespace Nethermind.Taiko;

/// <remarks>
/// On Taiko, every block header has <c>TotalDifficulty == 0</c> (post-merge from genesis —
/// taiko-geth's <c>params/taiko_config.go</c> sets <c>TerminalTotalDifficulty: 0</c>),
/// while <c>Difficulty</c> is repurposed as the per-block ZK gas used (non-consensus).
/// The default <see cref="CumulativeTotalDifficultyStrategy"/> computes
/// <c>(TD ?? 0) - Difficulty</c> and underflows during fast-block header insertion,
/// stopping snap-sync. Mirror taiko-geth's behaviour: parent TD is always zero on Taiko.
/// </remarks>
public sealed class TaikoSynchronizerModule : Module
{
    protected override void Load(ContainerBuilder builder) => builder
        .AddSingleton<ITotalDifficultyStrategy, ZeroTotalDifficultyStrategy>()
        .AddDecorator<ISyncProgressResolver>((ctx, inner) =>
            new TaikoSyncProgressResolver(ctx.Resolve<IBlockTree>(), inner))
        .AddSingleton<TaikoBeaconHeadAdvancer>();
}
