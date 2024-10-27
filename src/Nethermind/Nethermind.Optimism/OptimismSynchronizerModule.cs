// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Synchronization;

namespace Nethermind.Optimism;

/// <remarks>
/// In Optimism Mainnet, the <see cref="BlockHeader.TotalDifficulty"/> gets resetted to <c>0</c> in the Bedrock block unlike other chains that went through The Merge fork.
/// Calculation is still the same: the current block's <see cref="BlockHeader.TotalDifficulty"/> is the parent's <see cref="BlockHeader.TotalDifficulty"/> plus the current block's <see cref="BlockHeader.Difficulty"/>.
/// <seealso href="https://github.com/NethermindEth/nethermind/issues/7626"/>
/// </remarks>
public sealed class OptimismSynchronizerModule(long bedrockBlockNumber, ISpecProvider provider) : Module
{
    private const ulong OptimismMainnetChainId = 0xA;

    protected override void Load(ContainerBuilder builder)
    {
        if (provider.ChainId == OptimismMainnetChainId)
        {
            builder.AddSingleton<ITotalDifficultyStrategy>(
                new FixedTotalDifficultyStrategy(
                    new CumulativeTotalDifficultyStrategy(),
                    fixesBlockNumber: bedrockBlockNumber - 1,
                    toTotalDifficulty: provider.TerminalTotalDifficulty ?? throw new ArgumentNullException(nameof(provider.TerminalTotalDifficulty))
                )
            );
        }
    }
}
