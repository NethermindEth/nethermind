// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.GasPolicy;
using Nethermind.Int256;

namespace Nethermind.Evm;

public static partial class EvmInstructions
{
    /// <summary>
    /// Answers a depth-1 call from the subcall memo when every earlier sibling of this frame left
    /// no effects, mirroring the inline-precompile completion: charge the recorded gas out of the
    /// reserved child gas, surface the recorded revert payload as return data, copy it into the
    /// caller's output window (pre-charged by the call prologue) and push the failure status the
    /// recorded child produced. On a miss the pending key is parked on the machine for the frame
    /// path to tag the child, so a clean revert records itself at the merge.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool TryAnswerSubcallFromMemo<TGasPolicy, TTracingInst>(
        VirtualMachine<TGasPolicy> vm,
        ref EvmStack stack,
        ref TGasPolicy gas,
        in UInt256 dataOffset,
        UInt256 dataLength,
        in UInt256 outputOffset,
        UInt256 outputLength,
        Address target,
        Address codeSource,
        in UInt256 callValue,
        ulong gasLimitUl,
        out EvmExceptionType result)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        result = default;
        vm.PendingSubcallMemoEligible = false;
        if (TTracingInst.IsActive || vm.TxTracer.IsTracingActions || !callValue.IsZero
            || vm.VmState.Env.CallDepth != 0 || !vm.SubcallPrefixClean)
        {
            return false;
        }

        if (!vm.VmState.Memory.TryLoad(in dataOffset, dataLength, out ReadOnlyMemory<byte> callData))
        {
            // The frame path fails the same way; let it produce the failure.
            return false;
        }

        ref readonly TxExecutionContext txCtx = ref vm.TxExecutionContext;
        ValueHash256 key = SubcallMemo.ComputeKey(
            vm.BlockExecutionContext.Header.Hash, in txCtx.Origin, in txCtx.GasPrice,
            codeSource, target, gasLimitUl, callData.Span);
        if (!SubcallMemo.TryGet(in key, out SubcallMemo.Entry entry))
        {
            vm.PendingSubcallMemoKey = key;
            vm.PendingSubcallMemoGasGiven = gasLimitUl;
            vm.PendingSubcallMemoEligible = true;
            return false;
        }

        TGasPolicy childGas = TGasPolicy.CreateChildFrameGas(ref gas, gasLimitUl);
        if (!TGasPolicy.TryConsume(ref childGas, entry.GasSpent))
        {
            // Unreachable while the key includes the forwarded gas; run the real frame.
            TGasPolicy.Refund(ref gas, in childGas);
            return false;
        }

        TGasPolicy.Refund(ref gas, in childGas);
        ReadOnlyMemory<byte> outputData = entry.Output;
        vm.ReturnDataBuffer = outputData;

        int copyLength = outputData.Length;
        if (outputLength < (UInt256)copyLength)
            copyLength = (int)outputLength.ToLong();

        if (copyLength > 0 && !vm.VmState.Memory.TrySave(in outputOffset, outputData.Span[..copyLength]))
        {
            result = EvmExceptionType.OutOfGas;
            return true;
        }

        vm.ReturnData = null;
        result = stack.PushZero<TTracingInst>();
        return true;
    }
}
