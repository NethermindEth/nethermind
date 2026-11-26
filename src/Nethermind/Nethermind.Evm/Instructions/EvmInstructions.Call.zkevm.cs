// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.Evm;

public static partial class EvmInstructions
{
    // The zkEVM guest handles STATICCALL precompiles through its dedicated InlinePrecompileCall path in
    // CreateFullCallFrame, so the inline fast path always declines here.
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
        result = default;
        return false;
    }

    // The guest has no icache and counts instructions, so a NoInlining CreateFullCallFrame and its wide argument
    // marshalling are pure overhead on every CALL; inline it, pulling the hot precompile path in too.
    private const MethodImplOptions FullCallFrameInlining = MethodImplOptions.AggressiveInlining;

    private const bool InlinesPrecompileFrames = true;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static partial EvmExceptionType InlinePrecompileFrame<TGasPolicy, TOpCall, TTracingInst>(
        VirtualMachine<TGasPolicy> vm,
        ExecutionEnvironment callEnv,
        TGasPolicy childGas,
        long outputOffset,
        long outputLength,
        in Snapshot snapshot,
        ref EvmStack stack,
        bool newAccountCharged)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TOpCall : struct, IOpCall
        where TTracingInst : struct, IFlag =>
        vm.InlinePrecompileCall<TTracingInst>(
            callEnv,
            childGas,
            outputOffset,
            outputLength,
            TOpCall.ExecutionType,
            TOpCall.ExecutionType == ExecutionType.STATICCALL || vm.VmState.IsStatic,
            in snapshot,
            ref stack,
            newAccountCharged);
}
