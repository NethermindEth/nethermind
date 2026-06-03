// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core;

/// <summary>
/// Limits derived from surveying all supported chain configs.
/// </summary>
public static class SupportedChainLimits
{
    /// <summary> Highest <c>TargetBlockGasLimit</c> across all runner configs (atm: XDC). </summary>
    public const int MaxTargetBlockGasLimit = 420_000_000;

    /// <summary> Highest genesis <c>gasLimit</c> across all chain specs (atm: JOC, rounded up). </summary>
    public const int MaxGenesisBlockGasLimit = 480_000_000;

    /// <summary>
    /// The effective maximum block gas limit across all supported chains -
    /// the larger of <see cref="MaxTargetBlockGasLimit"/> and <see cref="MaxGenesisBlockGasLimit"/>.
    /// </summary>
    public const int MaxBlockGasLimit = MaxTargetBlockGasLimit > MaxGenesisBlockGasLimit
        ? MaxTargetBlockGasLimit
        : MaxGenesisBlockGasLimit;
}
