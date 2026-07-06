// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.State;
using Nethermind.Int256;
using static Nethermind.Evm.VirtualMachineStatics;

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
        if (TTracingInst.IsActive || vm.TxTracer.IsTracingActions || !vm.CanExecutePrecompileCallDirectly(precompile, codeSource))
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

        if (!TGasPolicy.ConsumePrecompileGas(ref childGas, precompile, callData, spec))
        {
            TGasPolicy.RestoreChildStateGasOnHalt(ref gas, in childGas);
            vm.ReturnDataBuffer = Array.Empty<byte>();
            vm.ReturnData = null;
            result = stack.PushZero<TTracingInst>();
            return true;
        }

        if (!(vm.TryRunPrecompileDirectly(precompile, callData, spec, out Result<byte[]> output) && output))
        {
            TGasPolicy.SetOutOfGas(ref childGas);
            TGasPolicy.RestoreChildStateGasOnHalt(ref gas, in childGas);
            vm.ReturnDataBuffer = Array.Empty<byte>();
            vm.ReturnData = null;
            result = stack.PushZero<TTracingInst>();
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
            ZeroPaddedSpan callOutput = outputData.Span.SliceWithZeroPadding(0, copyLength);
            if (!vm.VmState.Memory.TrySave(in outputOffset, in callOutput))
            {
                result = EvmExceptionType.OutOfGas;
                return true;
            }
        }

        vm.ReturnData = null;
        result = stack.PushBytes<TTracingInst>(StatusCode.SuccessBytes.Span);
        return true;
    }
}
