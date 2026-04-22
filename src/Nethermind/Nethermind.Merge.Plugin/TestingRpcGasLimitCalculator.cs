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
/// override by registering their own <see cref="ITestingRpcGasLimitCalculator"/>.
/// </summary>
public interface ITestingRpcGasLimitCalculator : IGasLimitCalculator;

/// <summary>
/// Default implementation that targets <see cref="DefaultGasLimit"/> when
/// <see cref="IBlocksConfig.TargetBlockGasLimit"/> is not configured.
/// </summary>
public class TestingRpcGasLimitCalculator(ISpecProvider specProvider, IBlocksConfig blocksConfig) : ITestingRpcGasLimitCalculator
{
    // Default gas limit target when not configured.
    internal const long DefaultGasLimit = 60_000_000;

    private readonly TargetAdjustedGasLimitCalculator _inner = new(
        specProvider, new BlocksConfig { TargetBlockGasLimit = blocksConfig.TargetBlockGasLimit ?? DefaultGasLimit });

    public long GetGasLimit(BlockHeader parentHeader) => _inner.GetGasLimit(parentHeader);
}
