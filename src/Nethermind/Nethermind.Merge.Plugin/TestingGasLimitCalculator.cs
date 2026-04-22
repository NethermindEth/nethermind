// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Merge.Plugin;

/// <summary>
/// Gas limit calculator scoped to <see cref="TestingRpcModule"/>, separate from the
/// global <see cref="IGasLimitCalculator"/> used by block production. Plugins can
/// override by registering their own <see cref="ITestingGasLimitCalculator"/>.
/// </summary>
public interface ITestingGasLimitCalculator : IGasLimitCalculator;

/// <summary>
/// Default implementation that targets <see cref="DefaultGasLimit"/> when
/// <see cref="IBlocksConfig.TargetBlockGasLimit"/> is not configured.
/// </summary>
public class TestingGasLimitCalculator(ISpecProvider specProvider, IBlocksConfig blocksConfig) : ITestingGasLimitCalculator
{
    // Matches the current mainnet gas limit target.
    internal const long DefaultGasLimit = 60_000_000;

    private readonly TargetAdjustedGasLimitCalculator _inner = new(
        specProvider, new BlocksConfig { TargetBlockGasLimit = blocksConfig.TargetBlockGasLimit ?? DefaultGasLimit });

    public long GetGasLimit(BlockHeader parentHeader) => _inner.GetGasLimit(parentHeader);
}
