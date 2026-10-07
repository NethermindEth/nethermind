// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Evm.GasPolicy;

namespace Nethermind.Evm;

public partial class VmState<TGasPolicy>
    where TGasPolicy : struct, IGasPolicy<TGasPolicy>
{
    // The EVM memory buffer lives on the per-tx scratch arena (reclaimed at reset), so a handle left from a
    // prior transaction dangles - reset it so the next growth allocates fresh. Mainline doesn't need this:
    // Dispose() clears _memory before the VmState returns to the pool.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    partial void ResetMemoryFromPriorTransaction() => _memory = new(_inlineMemory);
}
