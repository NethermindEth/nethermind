// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;

namespace Nethermind.Core;

public static class Eip7825Constants
{
    public static readonly ulong DefaultTxGasLimitCap = 16_777_216;

    /// <summary>The per-transaction gas limit cap: EIP-8037's absolute cap, EIP-7825's execution-gas cap before it, uncapped earlier.</summary>
    public static ulong GetTxGasLimitCap(this IReleaseSpec spec)
        => spec.IsEip8037Enabled ? Eip8037Constants.TxMaxTotalGasLimit
            : spec.IsEip7825Enabled ? DefaultTxGasLimitCap
            : ulong.MaxValue;
}
