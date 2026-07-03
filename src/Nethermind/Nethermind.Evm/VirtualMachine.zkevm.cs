// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.GasPolicy;

namespace Nethermind.Evm;

public unsafe partial class VirtualMachine<TGasPolicy> where TGasPolicy : struct, IGasPolicy<TGasPolicy>
{
    private delegate*<VirtualMachine<TGasPolicy>, ref EvmStack, ref TGasPolicy, ref int, EvmExceptionType>[] _opcodeMethods;

    // Cache the dispatch tables in plain per-TGasPolicy statics: the guest executes a single fork.
    // Mirrors the std build minus its periodic PGO-driven cache refresh, moot for the AOT-compiled guest.
    private static delegate*<VirtualMachine<TGasPolicy>, ref EvmStack, ref TGasPolicy, ref int, EvmExceptionType>[]? _opcodesNoTrace;
    private static delegate*<VirtualMachine<TGasPolicy>, ref EvmStack, ref TGasPolicy, ref int, EvmExceptionType>[]? _opcodesTraced;

    private partial void PrepareOpcodes<TTracingInst>(IReleaseSpec spec) where TTracingInst : struct, IFlag =>
        _opcodeMethods = !TTracingInst.IsActive
            ? _opcodesNoTrace ??= GenerateOpCodes<TTracingInst>(spec)
            : _opcodesTraced ??= GenerateOpCodes<TTracingInst>(spec);

    protected delegate*<VirtualMachine<TGasPolicy>, ref EvmStack, ref TGasPolicy, ref int, EvmExceptionType>[] GenerateOpCodes<TTracingInst>(IReleaseSpec spec) where TTracingInst : struct, IFlag =>
        EvmInstructions.GenerateOpCodes<TGasPolicy, TTracingInst>(spec);
}
