// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.GasPolicy;

namespace Nethermind.Evm;

public unsafe partial class VirtualMachine<TGasPolicy> where TGasPolicy : struct, IGasPolicy<TGasPolicy>
{
    // Weak keys: transient state-override specs in eth_simulateV1 must not be retained forever by this
    // process-wide cache.
    private static readonly ConditionalWeakTable<IReleaseSpec, OpcodeTable> _opcodeTablesBySpec = [];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private OpcodeTable GetOpcodeTable() =>
        _opcodeTablesBySpec.GetValue(Spec, static _ => new OpcodeTable());

    public object ReturnData { get; set; }

    protected virtual delegate*<VirtualMachine<TGasPolicy>, ref EvmStack, ref TGasPolicy, ref nint, EvmExceptionType>[] GenerateOpCodes<TTracingInst>(IReleaseSpec spec) where TTracingInst : struct, IFlag =>
        EvmInstructions.GenerateOpCodes<TGasPolicy, TTracingInst>(spec);
}
