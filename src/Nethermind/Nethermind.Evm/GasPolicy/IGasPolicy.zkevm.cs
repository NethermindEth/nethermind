// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.GasPolicy;

public partial interface IGasPolicy<TSelf>
{
    /// <summary>Overwrites the remaining execution gas without changing state-gas accounting.</summary>
    /// <remarks>Guest dispatch carries the remaining gas by value and writes it back to the frame's policy with this.</remarks>
    static abstract void SetRemainingGas(ref TSelf gas, ulong value);
}
