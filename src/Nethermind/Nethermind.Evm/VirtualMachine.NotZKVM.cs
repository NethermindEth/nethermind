// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#if !ZKVM
using System.Threading;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.GasPolicy;

namespace Nethermind.Evm;

public unsafe partial class VirtualMachine<TGasPolicy> where TGasPolicy : struct, IGasPolicy<TGasPolicy>
{
    private static long _txCount;
    private delegate*<VirtualMachine<TGasPolicy>, ref EvmStack, ref TGasPolicy, int, OpcodeResult>[] _opcodeMethods;

    private partial void PrepareOpcodes<TTracingInst>(IReleaseSpec spec) where TTracingInst : struct, IFlag
    {
        if (!TTracingInst.IsActive)
        {
            object? instructions = spec.EvmInstructionsNoTrace;
            if (instructions is not null && _txCount >= 500_000)
            {
                _opcodeMethods = (delegate*<VirtualMachine<TGasPolicy>, ref EvmStack, ref TGasPolicy, int, OpcodeResult>[])instructions;
                return;
            }
        }

        PrepareOpcodesSlow<TTracingInst>(spec);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void PrepareOpcodesSlow<TTracingInst>(IReleaseSpec spec) where TTracingInst : struct, IFlag
    {
        if (!TTracingInst.IsActive)
        {
            if (_txCount < 500_000 && Interlocked.Increment(ref _txCount) % 10_000 == 0)
            {
                if (_logger.IsDebug)
                {
                    _logger.Debug("Refreshing EVM instruction cache");
                }

                spec.EvmInstructionsNoTrace = GenerateOpCodes<TTracingInst>(spec);
            }

            _opcodeMethods = (delegate*<VirtualMachine<TGasPolicy>, ref EvmStack, ref TGasPolicy, int, OpcodeResult>[])(spec.EvmInstructionsNoTrace ??= GenerateOpCodes<TTracingInst>(spec));
        }
        else
        {
            _opcodeMethods = (delegate*<VirtualMachine<TGasPolicy>, ref EvmStack, ref TGasPolicy, int, OpcodeResult>[])(spec.EvmInstructionsTraced ??= GenerateOpCodes<TTracingInst>(spec));
        }
    }

    protected virtual delegate*<VirtualMachine<TGasPolicy>, ref EvmStack, ref TGasPolicy, int, OpcodeResult>[] GenerateOpCodes<TTracingInst>(IReleaseSpec spec) where TTracingInst : struct, IFlag =>
        EvmInstructions.GenerateOpCodes<TGasPolicy, TTracingInst>(spec);
}
#endif
