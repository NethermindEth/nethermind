// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Evm.GasPolicy;

namespace Nethermind.Evm;

public partial class VmState<TGasPolicy>
    where TGasPolicy : struct, IGasPolicy<TGasPolicy>
{
    private static readonly EvmObjectPool<VmState<TGasPolicy>> _statePool = new(
        maxShared: VirtualMachineStatics.MaxCallDepth * 2);
}
