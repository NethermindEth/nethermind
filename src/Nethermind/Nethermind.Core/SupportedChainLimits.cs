// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core;

/// <summary>
/// Limits derived from surveying all supported chain configs.
/// </summary>
public static class SupportedChainLimits
{
    /// <summary>
    /// Maximum allowed block gas limit across all supported chains.
    /// </summary>
    public const int MaxBlockGas = 1_000_000_000;
}
