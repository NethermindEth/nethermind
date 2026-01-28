// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#if ZKVM
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.GasPolicy;

namespace Nethermind.Evm;

public unsafe partial class VirtualMachine<TGasPolicy> where TGasPolicy : struct, IGasPolicy<TGasPolicy>
{
    private delegate*<VirtualMachine<TGasPolicy>, ref EvmStack, ref TGasPolicy, int, OpcodeResult>[] _opcodeMethods;

    private partial void PrepareOpcodes<TTracingInst>(IReleaseSpec spec) where TTracingInst : struct, IFlag
    {
        // For tracing-enabled execution, generate (if necessary) and cache the traced opcode set.
        _opcodeMethods = (delegate*<VirtualMachine<TGasPolicy>, ref EvmStack, ref TGasPolicy, int, OpcodeResult>[])(spec.EvmInstructionsTraced ??= GenerateOpCodes<TTracingInst>(spec));
    }

    protected delegate*<VirtualMachine<TGasPolicy>, ref EvmStack, ref TGasPolicy, int, OpcodeResult>[] GenerateOpCodes<TTracingInst>(IReleaseSpec spec) where TTracingInst : struct, IFlag =>
        EvmInstructions.GenerateOpCodes<TGasPolicy, OffFlag>(spec);
}
#endif
