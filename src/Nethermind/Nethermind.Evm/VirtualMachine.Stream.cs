// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Evm.CodeAnalysis;

namespace Nethermind.Evm;

/// <summary>
/// Process-wide switches for the preprocessed-stream interpreter; non-generic so every
/// <see cref="VirtualMachine{TGasPolicy}"/> instantiation shares one flag and one counter.
/// </summary>
public static class StreamInterpreter
{
    /// <summary>
    /// Rollout flag; off by default. Mutable so differential tests can flip interpreters
    /// in-process; read once per frame.
    /// </summary>
    public static bool Enabled =
        Environment.GetEnvironmentVariable("NETHERMIND_EVM_STREAM") == "1";

    /// <summary>
    /// Frames executed by the stream interpreter; engagement proof for tests and rollout.
    /// Unsynchronized — an approximate count is enough.
    /// </summary>
    public static long FramesExecuted;

    // TEMPORARY divergence diagnostics — remove before merge.
    internal static readonly bool Diagnose =
        Environment.GetEnvironmentVariable("NETHERMIND_STREAM_DIAG") == "1";

    internal static void Log(string file, int depth, int pc, Instruction instruction, long gas)
        => System.IO.File.AppendAllText(file, $"{depth} {pc} {instruction} {gas}\n");
}

public unsafe partial class VirtualMachine<TGasPolicy>
{

    /// <summary>
    /// Executes a frame over the preprocessed <see cref="InstructionStream"/> instead of the
    /// raw bytecode: per-basic-block static gas is charged once at each
    /// <see cref="StreamOpKind.BlockFirst"/> and the static-cost ops inside run their gas-free
    /// cores; every other op runs the standard table handler with the standard per-op epilogue.
    /// </summary>
    /// <remarks>
    /// Non-tracing executions only — callers gate on the tracing flag and a tip-fork dispatch
    /// fingerprint (the analyzer's in-block op set assumes Shanghai+ opcode semantics).
    /// When a block's full static cost exceeds remaining gas, the charge is skipped and the
    /// block's ops fall back to the metered table handlers so the halting op and failure type
    /// match per-op interpretation exactly.
    /// </remarks>
    [SkipLocalsInit]
    private CallResult RunStream<TCancelable>(
        InstructionStream stream,
        scoped ref EvmStack stack,
        scoped ref TGasPolicy gas)
        where TCancelable : struct, IFlag
    {
        ReturnData = null;
        EvmExceptionType exceptionType = EvmExceptionType.None;
        StreamInterpreter.FramesExecuted++;

        int programCounter = VmState.ProgramCounter;
        delegate*<VirtualMachine<TGasPolicy>, ref EvmStack, ref TGasPolicy, ref int, EvmExceptionType>[] opcodeArray = _opcodeMethods;
        StreamOp[] ops = stream.Ops;
        long[] blockGas = stream.BlockGas;
        ushort[] pcToEntry = stream.PcToEntry;
        int callDepth = VmState.Env.CallDepth;
        int opCodeCount = 0;
        // Set when a block's precharge would not fit the remaining gas; the frame then runs
        // metered to its death so the exact failing op and failure type are preserved.
        bool metered = false;

        // Resume pcs (after a CALL-family suspension) are instruction starts at most one past
        // the end of code; the bound guards a truncated trailing PUSH having overshot it.
        int entryIndex = programCounter == 0
            ? 0
            : (uint)programCounter < (uint)pcToEntry.Length ? pcToEntry[programCounter] : ops.Length;
        fixed (delegate*<VirtualMachine<TGasPolicy>, ref EvmStack, ref TGasPolicy, ref int, EvmExceptionType>* opcodeMethods = &opcodeArray[0])
        {
            while ((uint)entryIndex < (uint)ops.Length)
            {
                ref readonly StreamOp entry = ref ops[entryIndex];
                Instruction instruction = (Instruction)entry.Opcode;

                if (entry.Kind == StreamOpKind.BlockFirst)
                {
                    long cost = blockGas[entry.BlockIndex];
                    if (TGasPolicy.GetRemainingGas(in gas) >= cost)
                    {
                        TGasPolicy.Consume(ref gas, cost);
                        metered = false;
                    }
                    else
                    {
                        metered = true;
                    }
                }

                if (TCancelable.IsActive && (opCodeCount & CancellationCheckMask) == 0 && _txTracer.IsCancelled)
                    ThrowStreamOperationCanceledException();

                TGasPolicy.OnBeforeInstructionTrace(in gas, programCounter, instruction, callDepth);
                if (StreamInterpreter.Diagnose)
                    StreamInterpreter.Log("/tmp/diag-stream.log", callDepth, programCounter, instruction, TGasPolicy.GetRemainingGas(in gas));
                programCounter++;
                opCodeCount++;

                if (entry.Kind <= StreamOpKind.InBlock && !metered)
                {
                    // Gas for this op was charged at the block start; run the gas-free core.
                    // MUST stay inline in the loop, same JIT constraint as the specialized
                    // dispatch switch in RunByteCodeCore.
                    switch (instruction)
                    {
                        case Instruction.ADD:
                            exceptionType = EvmInstructions.Math2ParamCore<EvmInstructions.OpAdd, OffFlag>(ref stack);
                            break;
                        case Instruction.SUB:
                            exceptionType = EvmInstructions.Math2ParamCore<EvmInstructions.OpSub, OffFlag>(ref stack);
                            break;
                        case Instruction.MUL:
                            exceptionType = EvmInstructions.Math2ParamCore<EvmInstructions.OpMul, OffFlag>(ref stack);
                            break;
                        case Instruction.DIV:
                            exceptionType = EvmInstructions.Math2ParamCore<EvmInstructions.OpDiv, OffFlag>(ref stack);
                            break;
                        case Instruction.SDIV:
                            exceptionType = EvmInstructions.Math2ParamCore<EvmInstructions.OpSDiv, OffFlag>(ref stack);
                            break;
                        case Instruction.MOD:
                            exceptionType = EvmInstructions.Math2ParamCore<EvmInstructions.OpMod, OffFlag>(ref stack);
                            break;
                        case Instruction.SMOD:
                            exceptionType = EvmInstructions.Math2ParamCore<EvmInstructions.OpSMod, OffFlag>(ref stack);
                            break;
                        case Instruction.LT:
                            exceptionType = EvmInstructions.Math2ParamCore<EvmInstructions.OpLt, OffFlag>(ref stack);
                            break;
                        case Instruction.GT:
                            exceptionType = EvmInstructions.Math2ParamCore<EvmInstructions.OpGt, OffFlag>(ref stack);
                            break;
                        case Instruction.SLT:
                            exceptionType = EvmInstructions.Math2ParamCore<EvmInstructions.OpSLt, OffFlag>(ref stack);
                            break;
                        case Instruction.SGT:
                            exceptionType = EvmInstructions.Math2ParamCore<EvmInstructions.OpSGt, OffFlag>(ref stack);
                            break;
                        case Instruction.EQ:
                            exceptionType = EvmInstructions.BitwiseCore<EvmInstructions.OpBitwiseEq>(ref stack);
                            break;
                        case Instruction.AND:
                            exceptionType = EvmInstructions.BitwiseCore<EvmInstructions.OpBitwiseAnd>(ref stack);
                            break;
                        case Instruction.OR:
                            exceptionType = EvmInstructions.BitwiseCore<EvmInstructions.OpBitwiseOr>(ref stack);
                            break;
                        case Instruction.XOR:
                            exceptionType = EvmInstructions.BitwiseCore<EvmInstructions.OpBitwiseXor>(ref stack);
                            break;
                        case Instruction.ISZERO:
                            exceptionType = EvmInstructions.Math1ParamCore<EvmInstructions.OpIsZero>(ref stack);
                            break;
                        case Instruction.NOT:
                            exceptionType = EvmInstructions.Math1ParamCore<EvmInstructions.OpNot>(ref stack);
                            break;
                        case Instruction.SHL:
                            exceptionType = EvmInstructions.ShiftCore<EvmInstructions.OpShl, OffFlag>(ref stack);
                            break;
                        case Instruction.SHR:
                            exceptionType = EvmInstructions.ShiftCore<EvmInstructions.OpShr, OffFlag>(ref stack);
                            break;
                        case Instruction.POP:
                            exceptionType = stack.PopLimbo() ? EvmExceptionType.None : EvmExceptionType.StackUnderflow;
                            break;
                        case Instruction.PUSH0:
                            exceptionType = stack.PushZero<OffFlag>();
                            break;
                        case Instruction.PUSH1:
                        case >= Instruction.PUSH3 and <= Instruction.PUSH8:
                            // The analyzer pre-decoded the immediates (full-width only; a
                            // truncated trailing PUSH stays a boundary op).
                            exceptionType = stack.PushUInt64<OffFlag>(entry.Operand);
                            programCounter += instruction - Instruction.PUSH1 + 1;
                            break;
                        case >= Instruction.DUP1 and <= Instruction.DUP8:
                            exceptionType = stack.Dup<OffFlag>(instruction - Instruction.DUP1 + 1);
                            break;
                        case >= Instruction.SWAP1 and <= Instruction.SWAP8:
                            exceptionType = stack.Swap<OffFlag>(instruction - Instruction.SWAP1 + 2);
                            break;
                        case Instruction.JUMPDEST:
                            exceptionType = EvmExceptionType.None;
                            break;
                        default:
                            // The analyzer's in-block set diverged from this switch; the table
                            // handler is metered, so charge-free dispatch here would corrupt
                            // gas — fail loudly instead.
                            exceptionType = EvmExceptionType.BadInstruction;
                            Debug.Fail($"stream in-block op {instruction} has no gas-free core");
                            break;
                    }

                    if (exceptionType != EvmExceptionType.None)
                        break;

                    entryIndex++;
                    continue;
                }

                // Jump-class, boundary, and metered-fallback ops: standard handler, standard epilogue.
                exceptionType = opcodeMethods[(int)instruction](this, ref stack, ref gas, ref programCounter);

                if (TGasPolicy.GetRemainingGas(in gas) < 0)
                {
                    OpCodeCount += opCodeCount;
                    goto OutOfGas;
                }

                TGasPolicy.OnAfterInstructionTrace(in gas);

                if (exceptionType != EvmExceptionType.None)
                    break;

                if (ReturnData is not null)
                    break;

                // Table handlers may consume MORE than one instruction (fused PUSH2+JUMP,
                // EXTCODESIZE+ISZERO, and any future superinstruction), so after ANY table
                // call the entry index is recomputed from the landing pc — blind increment is
                // reserved for the check-free cores above, which provably advance exactly one
                // instruction. The landing pc is always an instruction start or past the end
                // of code (a truncated trailing PUSH advances past it — same clean exit as
                // the bytecode loop). A landing inside immediate bytes cannot happen while
                // ValidateJump holds — fail loudly rather than silently fall off the stream
                // as an empty success.
                if ((uint)programCounter >= (uint)pcToEntry.Length)
                {
                    entryIndex = ops.Length;
                    continue;
                }

                entryIndex = pcToEntry[programCounter];
                if (entryIndex == InstructionStream.InvalidEntry)
                {
                    exceptionType = EvmExceptionType.InvalidJumpDestination;
                    break;
                }

                // pc one past the last instruction maps one past the last entry: clean exit.
                if ((uint)entryIndex >= (uint)ops.Length)
                    continue;

                // A fused handler can land one instruction INTO a block (EXTCODESIZE+ISZERO
                // consumes the block's first op), skipping the BlockFirst precharge — the rest
                // of that block must then meter itself op by op.
                metered = ops[entryIndex].Kind == StreamOpKind.InBlock;
            }

            OpCodeCount += opCodeCount;
        }

        if (exceptionType is EvmExceptionType.None or EvmExceptionType.Stop or EvmExceptionType.Revert)
        {
            UpdateCurrentState(programCounter, in gas, stack.Head);
        }
        else
        {
            goto ReturnFailure;
        }

        if (exceptionType == EvmExceptionType.Revert)
            goto Revert;
        if (ReturnData is not null)
            goto DataReturn;

        return CallResult.Empty();

    DataReturn:
        if (ReturnData is byte[] data)
        {
            return new CallResult(data, null);
        }
        else if (ReturnData is VmState<TGasPolicy> state)
        {
            return new CallResult(state);
        }
        return new CallResult(ReturnDataBuffer, null);

    Revert:
        return new CallResult((byte[])ReturnData, null, shouldRevert: true, exceptionType);

    OutOfGas:
        TGasPolicy.SetOutOfGas(ref gas);
        exceptionType = EvmExceptionType.OutOfGas;
    ReturnFailure:
        _currentState.Gas = gas;
        return GetFailureReturn(TGasPolicy.GetRemainingGas(in gas), exceptionType);

        [DoesNotReturn]
        static void ThrowStreamOperationCanceledException() => throw new OperationCanceledException("Cancellation Requested");
    }
}
