// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;

namespace Nethermind.Core;

public static class Eip7825Constants
{
    public static readonly long DefaultTxGasLimitCap = 16_777_216;
    public static long GetTxGasLimitCap(IReleaseSpec spec)
        => spec.IsEip7825Enabled ? DefaultTxGasLimitCap : long.MaxValue;
}
