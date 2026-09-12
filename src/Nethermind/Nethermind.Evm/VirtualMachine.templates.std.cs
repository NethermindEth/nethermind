// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.GasPolicy;
using Nethermind.Int256;

namespace Nethermind.Evm;

/// <summary>
/// Entry into frames whose code matches a recognized template, which the zkEVM build does not compile.
/// </summary>
public partial class VirtualMachine<TGasPolicy>
{
    /// <summary>
    /// Runs the forwarding preamble of a recognized EIP-1167 minimal proxy, leaving the frame on its
    /// DELEGATECALL for the dispatch loop to execute.
    /// </summary>
    /// <remarks>
    /// Charges the skipped opcodes and reproduces their effects — the calldata copied to memory and the
    /// call's six operands on the stack — but leaves the call itself to the ordinary opcode handler, so
    /// account access, EIP-7702 delegation and the 63/64 gas reservation keep following the fork's rules
    /// rather than a copy of them.
    /// </remarks>
    private static EvmExceptionType EnterMinimalProxy(VmState<TGasPolicy> vmState, MinimalProxy proxy, Address target, ref EvmStack stack, ref TGasPolicy gas)
    {
        ExecutionEnvironment env = vmState.Env;
        ReadOnlySpan<byte> callData = env.InputData.Span;
        UInt256 callDataLength = (UInt256)(ulong)callData.Length;

        if (!TGasPolicy.UpdateGas(ref gas, proxy.GasBeforeCall)) return EvmExceptionType.OutOfGas;

        // CALLDATACOPY(0, 0, CALLDATASIZE), whose base cost is already part of GasBeforeCall.
        if (!TGasPolicy.TryConsumeMemoryCopy(ref gas, EvmCalculations.Div32Ceiling(in callDataLength, out _)))
            return EvmExceptionType.OutOfGas;

        if (!callDataLength.IsZero)
        {
            if (!TGasPolicy.UpdateMemoryCost(ref gas, UInt256.Zero, in callDataLength, ref vmState.Memory))
                return EvmExceptionType.OutOfGas;

            vmState.Memory.CopyFromZeroExtendedAfterGas(UInt256.Zero, callData, UInt256.Zero, callData.Length);
        }

        // Beneath DELEGATECALL(GAS, target, 0, CALLDATASIZE, 0, 0)'s six operands the preamble leaves the
        // variant's own leading words, which the trailing RETURN and REVERT read as their offset and size.
        // The GAS operand is the gas remaining here, which is what the skipped GAS opcode would push.
        for (int i = 0; i < proxy.LeadingWords; i++)
        {
            if (stack.PushZero<OffFlag, OnFlag>() != EvmExceptionType.None) return EvmExceptionType.StackOverflow;
        }

        if (stack.PushZero<OffFlag, OnFlag>() != EvmExceptionType.None ||
            stack.PushZero<OffFlag, OnFlag>() != EvmExceptionType.None ||
            stack.PushUInt256<OffFlag>(in callDataLength) != EvmExceptionType.None ||
            stack.PushZero<OffFlag, OnFlag>() != EvmExceptionType.None ||
            stack.PushAddress<OffFlag>(target) != EvmExceptionType.None ||
            stack.PushUInt64<OffFlag, OnFlag>(TGasPolicy.GetRemainingGas(in gas)) != EvmExceptionType.None)
        {
            return EvmExceptionType.StackOverflow;
        }

        vmState.ProgramCounter = proxy.DelegateCallProgramCounter;
        return EvmExceptionType.None;
    }

    /// <summary>
    /// Finishes a minimal proxy frame once its DELEGATECALL has returned, bubbling the callee's output
    /// as the proxy's own return or revert data.
    /// </summary>
    /// <remarks>
    /// The proxy's memory holds nothing any caller can observe — the trailing RETURNDATACOPY only stages
    /// the bytes the RETURN immediately hands back — so the copy is charged but not performed, and the
    /// return buffer is bubbled up directly.
    /// </remarks>
    private CallResult CompleteMinimalProxy(VmState<TGasPolicy> vmState, MinimalProxy proxy, bool success, ref TGasPolicy gas)
    {
        UInt256 returnDataLength = (UInt256)(ulong)ReturnDataBuffer.Length;

        if (!TGasPolicy.UpdateGas(ref gas, proxy.GasAfterCall) ||
            !TGasPolicy.TryConsumeMemoryCopy(ref gas, EvmCalculations.Div32Ceiling(in returnDataLength, out _)))
            goto OutOfGas;

        if (!returnDataLength.IsZero &&
            !TGasPolicy.UpdateMemoryCost(ref gas, UInt256.Zero, in returnDataLength, ref vmState.Memory))
            goto OutOfGas;

        if (!TGasPolicy.UpdateGas(ref gas, success ? proxy.GasOnSuccess : proxy.GasOnFailure))
            goto OutOfGas;

        return success
            ? new CallResult(ReturnDataBuffer.ToArray(), null)
            : new CallResult(ReturnDataBuffer.ToArray(), null, shouldRevert: true, EvmExceptionType.Revert);

    OutOfGas:
        TGasPolicy.ClearExecutionGas(ref gas);
        return GetFailureReturn(TGasPolicy.GetRemainingGas(in gas), EvmExceptionType.OutOfGas);
    }

    /// <summary>
    /// Resumes a recognized Solidity dispatcher at the function body its selector picks, instead of
    /// running the preamble and comparison chain opcode by opcode.
    /// </summary>
    /// <remarks>
    /// Charges what the skipped opcodes cost and reproduces what they leave behind: the selector on the
    /// stack, the free-memory pointer in memory, and the program counter at the body's JUMPDEST, which
    /// the dispatch loop then charges for as usual. Every rejection happens before any of that is
    /// applied, so declining is always equivalent to never having been called.
    /// </remarks>
    private static void TryEnterFunctionBody(VmState<TGasPolicy> vmState, IReleaseSpec spec, ref EvmStack stack, ref TGasPolicy gas)
    {
        ExecutionEnvironment env = vmState.Env;
        SelectorDispatch? dispatch = env.CodeInfo.Template.SelectorDispatch;
        if (dispatch is null || !dispatch.IsEnabled(spec)) return;

        ReadOnlySpan<byte> input = env.InputData.Span;
        if (input.Length < sizeof(uint)) return;

        // The preamble's guard reverts a value-bearing call; leave that to the dispatch loop.
        if (dispatch.RejectsCallValue && !env.Value.IsZero) return;

        uint selector = BinaryPrimitives.ReadUInt32BigEndian(input);
        if (!dispatch.TryResolve(selector, !env.Value.IsZero, out int programCounter, out ulong gasCost)) return;

        // Checked up front so the charge below cannot fail once the frame has been moved to the body.
        if (TGasPolicy.GetRemainingGas(in gas) < gasCost) return;

        Span<byte> freeMemoryPointer = stackalloc byte[EvmPooledMemory.WordSize];
        freeMemoryPointer.Clear();
        BinaryPrimitives.WriteUInt32BigEndian(freeMemoryPointer[^sizeof(uint)..], SelectorDispatch.InitialFreeMemoryPointer);

        bool saved = vmState.Memory.TrySaveWord(SelectorDispatch.FreeMemoryPointerSlot, freeMemoryPointer);
        Debug.Assert(saved, "The free-memory-pointer slot is a fixed low offset that cannot overflow memory.");

        EvmExceptionType pushed = stack.PushUInt32<OffFlag, OnFlag>(selector);
        Debug.Assert(pushed == EvmExceptionType.None, "A frame's stack is empty on entry, so the selector always fits.");

        vmState.ProgramCounter = programCounter;
        TGasPolicy.UpdateGas(ref gas, gasCost);
    }
}
