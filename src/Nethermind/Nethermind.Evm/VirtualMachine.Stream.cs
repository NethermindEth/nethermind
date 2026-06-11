// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
    /// Folds PUSH+op pairs into single const-op entries at analysis time; off by default —
    /// measured net-negative inside the switch-based executor (the per-op pair check and the
    /// doubled dispatch switch cost more than the saved dispatches). Kept for the
    /// direct-threading executor where fusion economics work.
    /// </summary>
    public static bool FusionEnabled =
        Environment.GetEnvironmentVariable("NETHERMIND_EVM_STREAM_FUSION") == "1";

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
    /// raw bytecode: per-basic-block static gas is charged once at each block's first entry,
    /// static-cost ops run gas-free cores, fused PUSH+op pairs run against their pre-decoded
    /// constant in a single dispatch, and every other op runs the standard table handler with
    /// the standard per-op epilogue.
    /// </summary>
    /// <remarks>
    /// Non-tracing executions only — callers gate on the tracing flag and a tip-fork dispatch
    /// fingerprint (the analyzer's in-block op set assumes Shanghai+ opcode semantics).
    /// When a block's full static cost exceeds remaining gas — or execution lands past a
    /// block's charging entry — the block runs through the metered micro-loop, which reads
    /// raw code and dispatches the table per op, so the halting op and failure type match
    /// per-op interpretation exactly.
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
        Nethermind.Int256.UInt256[] constants = stream.Constants;
        ushort[] pcToEntry = stream.PcToEntry;
        ref byte code = ref stack.Code;
        uint codeLength = (uint)stack.CodeLength;
        int callDepth = VmState.Env.CallDepth;
        int opCodeCount = 0;
        // Set when a block's precharge would not fit the remaining gas (or execution landed
        // past the charging entry); the block then runs the metered micro-loop so the exact
        // failing op and failure type are preserved.
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

                if (entry.Kind < StreamOpKind.Boundary)
                {
                    if (entry.Kind <= StreamOpKind.FusedBlockFirst)
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

                    if (metered)
                    {
                        programCounter = entry.Pc;
                        MeteredOutcome outcome = RunMeteredSegment<TCancelable>(stream, ref stack, ref gas, ref programCounter, ref opCodeCount, ref entryIndex, ref metered, ref exceptionType, callDepth);
                        if (outcome == MeteredOutcome.Continue)
                            continue;
                        if (outcome == MeteredOutcome.OutOfGas)
                        {
                            OpCodeCount += opCodeCount;
                            goto OutOfGas;
                        }

                        break;
                    }

                    if (TCancelable.IsActive && (opCodeCount & CancellationCheckMask) == 0 && _txTracer.IsCancelled)
                        ThrowStreamOperationCanceledException();

                    TGasPolicy.OnBeforeInstructionTrace(in gas, entry.Pc, instruction, callDepth);
                    if (StreamInterpreter.Diagnose)
                        StreamInterpreter.Log("/tmp/diag-stream.log", callDepth, entry.Pc, instruction, TGasPolicy.GetRemainingGas(in gas));
                    opCodeCount += 1 + ((byte)entry.Kind & 1);

                    // One switch for plain in-block ops AND fused pairs (virtual opcodes):
                    // a single dense dispatch with no per-op pair branch. Gas was charged at
                    // the block's first entry; the cores are gas-free.
                    // MUST stay inline in the loop, same JIT constraint as the specialized
                    // dispatch switch in RunByteCodeCore.
                    switch (instruction)
                    {
                        case (Instruction)FusedOpcode.Add:
                            exceptionType = EvmInstructions.FusedConstBinaryCore<EvmInstructions.OpAdd>(ref stack, in constants[(int)entry.Operand]);
                            break;
                        case (Instruction)FusedOpcode.Sub:
                            exceptionType = EvmInstructions.FusedConstBinaryCore<EvmInstructions.OpSub>(ref stack, in constants[(int)entry.Operand]);
                            break;
                        case (Instruction)FusedOpcode.Mul:
                            exceptionType = EvmInstructions.FusedConstBinaryCore<EvmInstructions.OpMul>(ref stack, in constants[(int)entry.Operand]);
                            break;
                        case (Instruction)FusedOpcode.Div:
                            exceptionType = EvmInstructions.FusedConstBinaryCore<EvmInstructions.OpDiv>(ref stack, in constants[(int)entry.Operand]);
                            break;
                        case (Instruction)FusedOpcode.SDiv:
                            exceptionType = EvmInstructions.FusedConstBinaryCore<EvmInstructions.OpSDiv>(ref stack, in constants[(int)entry.Operand]);
                            break;
                        case (Instruction)FusedOpcode.Mod:
                            exceptionType = EvmInstructions.FusedConstBinaryCore<EvmInstructions.OpMod>(ref stack, in constants[(int)entry.Operand]);
                            break;
                        case (Instruction)FusedOpcode.SMod:
                            exceptionType = EvmInstructions.FusedConstBinaryCore<EvmInstructions.OpSMod>(ref stack, in constants[(int)entry.Operand]);
                            break;
                        case (Instruction)FusedOpcode.Lt:
                            exceptionType = EvmInstructions.FusedConstBinaryCore<EvmInstructions.OpLt>(ref stack, in constants[(int)entry.Operand]);
                            break;
                        case (Instruction)FusedOpcode.Gt:
                            exceptionType = EvmInstructions.FusedConstBinaryCore<EvmInstructions.OpGt>(ref stack, in constants[(int)entry.Operand]);
                            break;
                        case (Instruction)FusedOpcode.SLt:
                            exceptionType = EvmInstructions.FusedConstBinaryCore<EvmInstructions.OpSLt>(ref stack, in constants[(int)entry.Operand]);
                            break;
                        case (Instruction)FusedOpcode.SGt:
                            exceptionType = EvmInstructions.FusedConstBinaryCore<EvmInstructions.OpSGt>(ref stack, in constants[(int)entry.Operand]);
                            break;
                        case (Instruction)FusedOpcode.Eq:
                            exceptionType = EvmInstructions.FusedConstBinaryCore<EvmInstructions.OpEqFused>(ref stack, in constants[(int)entry.Operand]);
                            break;
                        case (Instruction)FusedOpcode.And:
                            exceptionType = EvmInstructions.FusedConstBinaryCore<EvmInstructions.OpAndFused>(ref stack, in constants[(int)entry.Operand]);
                            break;
                        case (Instruction)FusedOpcode.Or:
                            exceptionType = EvmInstructions.FusedConstBinaryCore<EvmInstructions.OpOrFused>(ref stack, in constants[(int)entry.Operand]);
                            break;
                        case (Instruction)FusedOpcode.Xor:
                            exceptionType = EvmInstructions.FusedConstBinaryCore<EvmInstructions.OpXorFused>(ref stack, in constants[(int)entry.Operand]);
                            break;
                        case (Instruction)FusedOpcode.Shl:
                            exceptionType = EvmInstructions.FusedConstShiftCore<EvmInstructions.OpShl>(ref stack, in constants[(int)entry.Operand]);
                            break;
                        case (Instruction)FusedOpcode.Shr:
                            exceptionType = EvmInstructions.FusedConstShiftCore<EvmInstructions.OpShr>(ref stack, in constants[(int)entry.Operand]);
                            break;
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
                            break;
                        case >= Instruction.PUSH9 and <= Instruction.PUSH32:
                            exceptionType = stack.PushUInt256<OffFlag>(in constants[(int)entry.Operand]);
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
                            // The analyzer's in-block set diverged from this switch; the
                            // table handler is metered, so charge-free dispatch here would
                            // corrupt gas — fail loudly instead.
                            exceptionType = EvmExceptionType.BadInstruction;
                            System.Diagnostics.Debug.Fail($"stream in-block op {instruction} has no gas-free core");
                            break;
                    }

                    if (exceptionType != EvmExceptionType.None)
                        break;

                    programCounter = entry.Pc + entry.Advance;
                    entryIndex++;
                    continue;
                }

                // Boundary op: standard handler, standard epilogue, structured control
                // flow only — backward gotos make the loop irreducible and the JIT stops
                // optimizing the whole method.
                programCounter = entry.Pc;
                if (TCancelable.IsActive && (opCodeCount & CancellationCheckMask) == 0 && _txTracer.IsCancelled)
                    ThrowStreamOperationCanceledException();

                TGasPolicy.OnBeforeInstructionTrace(in gas, programCounter, instruction, callDepth);
                if (StreamInterpreter.Diagnose)
                    StreamInterpreter.Log("/tmp/diag-stream.log", callDepth, programCounter, instruction, TGasPolicy.GetRemainingGas(in gas));
                programCounter++;
                opCodeCount++;

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
                // EXTCODESIZE+ISZERO, and any future superinstruction), so the entry index
                // is recomputed from the landing pc. A landing past the end of code (a
                // truncated trailing PUSH overshoots it) exits as cleanly as the bytecode
                // loop's pc-bounded while.
                if ((uint)programCounter >= (uint)pcToEntry.Length)
                {
                    entryIndex = ops.Length;
                    continue;
                }

                int landing = pcToEntry[programCounter];
                if (landing == InstructionStream.InvalidEntry)
                {
                    // Nothing may land between entries: jumps land on JUMPDESTs and
                    // table-fused handlers land after the instructions they consumed — fail
                    // loudly rather than silently fall off the stream as an empty success.
                    exceptionType = EvmExceptionType.InvalidJumpDestination;
                    break;
                }

                entryIndex = landing;
                if ((uint)entryIndex < (uint)ops.Length)
                {
                    // A fused table handler can land one instruction INTO a block, skipping
                    // its charging entry — the rest of that block runs metered.
                    StreamOpKind landingKind = ops[entryIndex].Kind;
                    metered = landingKind is StreamOpKind.InBlock or StreamOpKind.FusedInBlock;
                }
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

    private enum MeteredOutcome : byte
    {
        /// <summary>Resume the stream loop at the updated entry index.</summary>
        Continue,
        /// <summary>Exit the stream loop (exception or frame switch recorded by the caller's refs).</summary>
        BreakLoop,
        /// <summary>Gas went negative; the caller takes its out-of-gas exit.</summary>
        OutOfGas,
    }

    /// <summary>
    /// Cold path: per-op metered execution over raw code for blocks whose precharge did not
    /// fit the remaining gas or that were entered past their charging entry. Exact per-op gas
    /// and failure semantics; immune to fused-pair merging because it reads bytes. Kept out
    /// of <see cref="RunStream{TCancelable}"/> so the hot loop stays small and reducible.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private MeteredOutcome RunMeteredSegment<TCancelable>(
        InstructionStream stream,
        scoped ref EvmStack stack,
        scoped ref TGasPolicy gas,
        ref int programCounter,
        ref int opCodeCount,
        ref int entryIndex,
        ref bool metered,
        ref EvmExceptionType exceptionType,
        int callDepth)
        where TCancelable : struct, IFlag
    {
        StreamOp[] ops = stream.Ops;
        ushort[] pcToEntry = stream.PcToEntry;
        ref byte code = ref stack.Code;
        uint codeLength = (uint)stack.CodeLength;
        delegate*<VirtualMachine<TGasPolicy>, ref EvmStack, ref TGasPolicy, ref int, EvmExceptionType>[] opcodeMethods = _opcodeMethods;

        while (true)
        {
            if ((uint)programCounter >= codeLength)
            {
                entryIndex = ops.Length;
                return MeteredOutcome.Continue;
            }

            Instruction instruction = (Instruction)Unsafe.Add(ref code, programCounter);

            if (TCancelable.IsActive && (opCodeCount & CancellationCheckMask) == 0 && _txTracer.IsCancelled)
                throw new OperationCanceledException("Cancellation Requested");

            TGasPolicy.OnBeforeInstructionTrace(in gas, programCounter, instruction, callDepth);
            if (StreamInterpreter.Diagnose)
                StreamInterpreter.Log("/tmp/diag-stream.log", callDepth, programCounter, instruction, TGasPolicy.GetRemainingGas(in gas));
            programCounter++;
            opCodeCount++;

            exceptionType = opcodeMethods[(int)instruction](this, ref stack, ref gas, ref programCounter);

            if (TGasPolicy.GetRemainingGas(in gas) < 0)
                return MeteredOutcome.OutOfGas;

            TGasPolicy.OnAfterInstructionTrace(in gas);

            if (exceptionType != EvmExceptionType.None)
                return MeteredOutcome.BreakLoop;

            if (ReturnData is not null)
                return MeteredOutcome.BreakLoop;

            if ((uint)programCounter >= (uint)pcToEntry.Length)
            {
                entryIndex = ops.Length;
                return MeteredOutcome.Continue;
            }

            int landing = pcToEntry[programCounter];
            if (landing == InstructionStream.InvalidEntry)
            {
                // Interior pc (inside a fused pair or instructions a table handler consumed):
                // keep stepping through raw code.
                continue;
            }

            entryIndex = landing;
            if ((uint)entryIndex >= (uint)ops.Length)
                return MeteredOutcome.Continue;

            StreamOpKind kind = ops[entryIndex].Kind;
            if (kind is StreamOpKind.InBlock or StreamOpKind.FusedInBlock)
                continue;

            // Reached a block-charging entry or a boundary op: hand back to the stream loop,
            // which re-evaluates the charge (and so whether metering must continue).
            metered = false;
            return MeteredOutcome.Continue;
        }
    }
}
