// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Optimism;

/// <summary>
/// https://github.com/ethereum-optimism/specs/blob/main/specs/protocol/preinstalls.md
/// </summary>
public static class PreInstalls
{
    /// <summary>
    /// https://github.com/ethereum-optimism/specs/blob/main/specs/protocol/preinstalls.md#create2deployer
    /// </summary>
    public static readonly Address Create2Deployer = new("0x13b0D85CcB8bf860b6b79AF3029fCA081AE9beF2");
}
