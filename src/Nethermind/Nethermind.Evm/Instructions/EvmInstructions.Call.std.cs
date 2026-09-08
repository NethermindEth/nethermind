// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.Evm;

public static partial class EvmInstructions
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static partial bool TryInlineStaticPrecompileCall<TGasPolicy, TTracingInst>(
        VirtualMachine<TGasPolicy> vm,
        ref EvmStack stack,
        ref TGasPolicy gas,
        in UInt256 dataOffset,
        UInt256 dataLength,
        in UInt256 outputOffset,
        UInt256 outputLength,
        IPrecompile precompile,
        Address target,
        Address codeSource,
        ulong gasLimitUl,
        out EvmExceptionType result)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        Debug.Assert(vm.ReturnData is null, "Inline precompiles continue the current opcode chain.");
        if (TTracingInst.IsActive || vm.IsTracingActions || !vm.CanExecutePrecompileCallDirectly(precompile, codeSource))
        {
            result = default;
            return false;
        }

        if (!vm.VmState.Memory.TryLoad(in dataOffset, dataLength, out ReadOnlyMemory<byte> callData))
        {
            result = EvmExceptionType.OutOfGas;
            return true;
        }

        TGasPolicy childGas = TGasPolicy.CreateChildFrameGas(ref gas, gasLimitUl);
        IReleaseSpec spec = vm.Spec;

        if (!TGasPolicy.TryConsumePrecompileGas(ref childGas, precompile, callData, spec))
        {
            TGasPolicy.RestoreChildStateGasOnHalt(ref gas, in childGas);
            vm.ReturnDataBuffer = default;
            result = stack.PushZero<TTracingInst, OnFlag>();
            return true;
        }

        if (!(vm.TryRunPrecompileDirectly(precompile, callData, spec, out Result<byte[]> output) && output))
        {
            TGasPolicy.ClearExecutionGas(ref childGas);
            TGasPolicy.RestoreChildStateGasOnHalt(ref gas, in childGas);
            vm.ReturnDataBuffer = default;
            result = stack.PushZero<TTracingInst, OnFlag>();
            return true;
        }

        vm.WorldState.AddToBalanceAndCreateIfNotExists(target, UInt256.Zero, spec);

        TGasPolicy.Refund(ref gas, in childGas);

        ReadOnlyMemory<byte> outputData = output.Data;
        vm.ReturnDataBuffer = outputData;

        int copyLength = outputData.Length;
        if (outputLength < (UInt256)copyLength)
            copyLength = (int)outputLength.ToLong();

        if (copyLength > 0)
        {
            if (!vm.VmState.Memory.TrySave(in outputOffset, outputData.Span[..copyLength]))
            {
                result = EvmExceptionType.OutOfGas;
                return true;
            }
        }

        result = stack.PushBytes<TTracingInst>(StatusCode.SuccessBytes.Span);
        return true;
    }
}
