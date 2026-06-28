// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.GasPolicy;

namespace Nethermind.Evm;

public unsafe partial class VirtualMachine<TGasPolicy> where TGasPolicy : struct, IGasPolicy<TGasPolicy>
{
    private delegate*<VirtualMachine<TGasPolicy>, ref EvmStack, ref TGasPolicy, ref int, EvmExceptionType>[] _opcodeMethods;

    // Select and lazily build the opcode dispatch table for the active tracing mode, caching each
    // mode separately on the spec. Mirrors the std build minus its periodic PGO-driven cache refresh,
    // which is moot for the AOT-compiled guest.
    private partial void PrepareOpcodes<TTracingInst>(IReleaseSpec spec) where TTracingInst : struct, IFlag
    {
        OpcodeTable table = _opcodeTablesBySpec.GetValue(spec, static _ => new OpcodeTable());
        _opcodeMethods = !TTracingInst.IsActive
            ? table.NoTrace ??= GenerateOpCodes<TTracingInst>(spec)
            : table.Traced ??= GenerateOpCodes<TTracingInst>(spec);
    }

    protected delegate*<VirtualMachine<TGasPolicy>, ref EvmStack, ref TGasPolicy, ref int, EvmExceptionType>[] GenerateOpCodes<TTracingInst>(IReleaseSpec spec) where TTracingInst : struct, IFlag =>
        EvmInstructions.GenerateOpCodes<TGasPolicy, TTracingInst>(spec);
}
