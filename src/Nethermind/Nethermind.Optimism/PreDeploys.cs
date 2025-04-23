// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Optimism;

/// <summary>
/// https://github.com/ethereum-optimism/specs/blob/main/specs/protocol/predeploys.md#overview
/// </summary>
public static class PreDeploys
{
    /// <summary>
    /// https://github.com/ethereum-optimism/specs/blob/main/specs/protocol/predeploys.md#l2tol1messagepasser
    /// </summary>
    public static readonly Address L2ToL1MessagePasser = new("0x4200000000000000000000000000000000000016");

    /// <summary>
    /// The receiver of the operator fee
    /// </summary>
    public static readonly Address OperatorFeeRecipient = new("0x420000000000000000000000000000000000001B");
}
