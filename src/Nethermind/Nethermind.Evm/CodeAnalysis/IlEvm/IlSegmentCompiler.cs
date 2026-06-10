// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Nethermind.Core;
using Nethermind.Evm.GasPolicy;
using Nethermind.Int256;

namespace Nethermind.Evm.CodeAnalysis.IlEvm;

/// <summary>
/// Signature of a compiled segment — the opcode-handler ABI plus the constants context bound
/// as the delegate target. The caller must satisfy the segment's preconditions
/// (<see cref="IlCompiledSegment.StackRequired"/>, <see cref="IlCompiledSegment.StackMaxGrowth"/>,
/// <see cref="IlCompiledSegment.StaticGas"/>) before invoking and must only invoke on
/// non-tracing executions. Pure compute always completes; embedded handler calls (memory,
/// keccak — dynamic gas) may return a non-None halt, which the caller handles exactly like an
/// interpreted handler's result. On success the program counter lands on
/// <see cref="IlCompiledSegment.ExitPc"/>; on a handler halt the frame is dead and the program
/// counter is unobservable.
/// </summary>
public delegate EvmExceptionType CompiledSegmentInvoke(VirtualMachine<EthereumGasPolicy> vm, ref EvmStack stack, ref EthereumGasPolicy gas, ref int programCounter, int entryIndex);

public sealed class IlCompiledSegment
{
    public required CompiledSegmentInvoke Invoke { get; init; }

    /// <summary>Program counter this segment must be entered at (its first opcode).</summary>
    public required int EntryPc { get; init; }

    /// <summary>Program counter after the compiled prefix; execution resumes in the interpreter here.</summary>
    public required int ExitPc { get; init; }

    public required int OpCount { get; init; }

    /// <summary>
    /// Dispatch preconditions: the caller must invoke the segment only when the stack holds at
    /// least <see cref="StackRequired"/> values, can grow by <see cref="StackMaxGrowth"/>, and
    /// at least <see cref="StaticGas"/> gas remains. When any precondition fails the caller
    /// falls through to the interpreter, which produces the exact per-opcode halt state —
    /// segments therefore only ever execute to completion.
    /// </summary>
    public required long StaticGas { get; init; }

    public required int StackRequired { get; init; }

    public required int StackMaxGrowth { get; init; }

    /// <summary>
    /// Which entry of the (possibly multi-entry) compiled region this segment represents;
    /// passed verbatim to <see cref="Invoke"/>. Single-block segments use 0.
    /// </summary>
    public int EntryIndex { get; init; }
}

/// <summary>
/// IL-EVM v0 segment compiler: turns the longest emittable prefix of a compilable
/// <see cref="BasicBlock"/> into RyuJIT-compiled code.
///
/// Emission model — symbolic stack:
/// the block's stack traffic is simulated at compile time. Entry operands are popped into
/// locals once, every intermediate value lives in a local (PUSH constants come from a captured
/// constants array; DUP/SWAP/POP become pure symbol manipulation and emit no IL), and the
/// surviving values are pushed back in one batch at the end. This is where the speedup lives:
/// per-opcode dispatch, per-opcode gas checks, and per-opcode stack traffic all disappear.
///
/// Equivalence argument — segments only run when they are guaranteed to succeed:
/// the dispatch site checks the segment's preconditions (entry stack depth, stack headroom,
/// remaining gas ≥ summed static gas) and falls through to the interpreter when any of them
/// fails, so every halt state (underflow, overflow, out-of-gas) is produced by the interpreter
/// itself with exact per-opcode gas accounting. PUSH immediates that run past the end of code
/// are likewise never emitted (the prefix stops; the interpreter executes them), sidestepping
/// truncation semantics entirely. A successfully invoked segment performs exactly the
/// interpreter's value transformations (the same Op*.Operation methods / mirrored intrinsics)
/// and consumes exactly the same gas.
/// </summary>
public static partial class IlSegmentCompiler
{
    // Segment economics: a segment pays a fixed boundary toll — every entry operand is popped
    // and every surviving value pushed back through big-endian word conversions, each costing
    // several interpreted-ops' worth of time. Mainnet measurement showed that compiling short,
    // stack-traffic-heavy blocks is a net LOSS versus the fused interpreter. Only compile when
    // the interior is long enough to amortize the boundary:
    //   ops >= MinimumPrefixOps  AND  ops >= BoundaryCostFactor × (entryPops + exitPushes).
    // Mutable so tests can exercise small segments; volatile for cross-thread visibility.
    public static volatile int MinimumPrefixOps = 8;
    public static volatile int BoundaryCostFactor = 3;

    /// <summary>Head may grow to MaxStackSize - RegisterLength; one more and the interpreter's push fails.</summary>
    public const int UsableStackSlots = EvmStack.MaxStackSize - EvmStack.RegisterLength;

    // RequireVoid is load-bearing: the emitted IL performs no Pop after this call, so a non-void
    // return would silently unbalance the evaluation stack (DynamicMethods skip verification).
    // If the signature ever changes, the field becomes null and TryCompile turns itself off.
    private static readonly MethodInfo? s_gasConsume = RequireVoid(typeof(EthereumGasPolicy).GetMethod(nameof(EthereumGasPolicy.Consume), [typeof(EthereumGasPolicy).MakeByRefType(), typeof(long)]));
    private static readonly MethodInfo? s_gasRemaining = typeof(EthereumGasPolicy).GetMethod(nameof(EthereumGasPolicy.GetRemainingGas), [typeof(EthereumGasPolicy).MakeByRefType()]);
    private static readonly FieldInfo? s_stackHead = typeof(EvmStack).GetField(nameof(EvmStack.Head));
    private static readonly MethodInfo? s_popUInt256 = typeof(EvmStack).GetMethod(nameof(EvmStack.PopUInt256), [typeof(UInt256).MakeByRefType()]);
    private static readonly MethodInfo? s_pushUInt256 = typeof(EvmStack).GetMethod(nameof(EvmStack.PushUInt256))?.MakeGenericMethod(typeof(OffFlag));
    private static readonly FieldInfo? s_constantsValues = typeof(SegmentConstants).GetField(SegmentConstants.ValuesFieldName, BindingFlags.NonPublic | BindingFlags.Instance);

    // Arithmetic and comparison route through the interpreter's own Op*.Operation methods —
    // single source of truth, including the EVM edge semantics (division by zero yields zero).
    private static readonly MethodInfo? s_add = OperationOf(typeof(EvmInstructions.OpAdd));
    private static readonly MethodInfo? s_subtract = OperationOf(typeof(EvmInstructions.OpSub));
    private static readonly MethodInfo? s_multiply = OperationOf(typeof(EvmInstructions.OpMul));
    private static readonly MethodInfo? s_divide = OperationOf(typeof(EvmInstructions.OpDiv));
    private static readonly MethodInfo? s_signedDivide = OperationOf(typeof(EvmInstructions.OpSDiv));
    private static readonly MethodInfo? s_mod = OperationOf(typeof(EvmInstructions.OpMod));
    private static readonly MethodInfo? s_signedMod = OperationOf(typeof(EvmInstructions.OpSMod));
    private static readonly MethodInfo? s_addMod = IntrinsicOf(nameof(IlEvmIntrinsics.AddMod));
    private static readonly MethodInfo? s_mulMod = IntrinsicOf(nameof(IlEvmIntrinsics.MulMod));
    private static readonly MethodInfo? s_lessThan = OperationOf(typeof(EvmInstructions.OpLt));
    private static readonly MethodInfo? s_greaterThan = OperationOf(typeof(EvmInstructions.OpGt));
    private static readonly MethodInfo? s_signedLessThan = OperationOf(typeof(EvmInstructions.OpSLt));
    private static readonly MethodInfo? s_signedGreaterThan = OperationOf(typeof(EvmInstructions.OpSGt));
    // Range-guarded and sign-aware opcodes go through IlEvmIntrinsics, which mirror the handlers.
    private static readonly MethodInfo? s_shl = IntrinsicOf(nameof(IlEvmIntrinsics.Shl));
    private static readonly MethodInfo? s_shr = IntrinsicOf(nameof(IlEvmIntrinsics.Shr));
    private static readonly MethodInfo? s_sar = IntrinsicOf(nameof(IlEvmIntrinsics.Sar));
    private static readonly MethodInfo? s_signExtend = IntrinsicOf(nameof(IlEvmIntrinsics.SignExtend));
    private static readonly MethodInfo? s_byte = IntrinsicOf(nameof(IlEvmIntrinsics.Byte));
    private static readonly MethodInfo? s_not = IntrinsicOf(nameof(IlEvmIntrinsics.Not));
    private static readonly MethodInfo? s_opAnd = BinaryOperator("op_BitwiseAnd");
    private static readonly MethodInfo? s_opOr = BinaryOperator("op_BitwiseOr");
    private static readonly MethodInfo? s_opXor = BinaryOperator("op_ExclusiveOr");
    private static readonly MethodInfo? s_opEquality = BinaryOperator("op_Equality");
    private static readonly MethodInfo? s_isZero = typeof(UInt256).GetProperty(nameof(UInt256.IsZero))?.GetGetMethod();
    private static readonly MethodInfo? s_fromUlong = ImplicitFromUlong();

    // Opcodes executed mid-segment by calling the interpreter's own handler (closed over
    // EthereumGasPolicy + OffFlag): the segment flushes just the op's operands to the real
    // stack, calls the handler (which charges its own static + dynamic gas and may halt), and
    // reloads any result into a local. This keeps memory/keccak from CUTTING segments — the
    // full-boundary toll is replaced by a few words of operand traffic.
    private readonly record struct HandlerOp(MethodInfo Method, int Pops, int Pushes);

    private static readonly HandlerOp? s_mload = HandlerOf(nameof(EvmInstructions.InstructionMLoad), pops: 1, pushes: 1);
    private static readonly HandlerOp? s_mstore = HandlerOf(nameof(EvmInstructions.InstructionMStore), pops: 2, pushes: 0);
    private static readonly HandlerOp? s_mstore8 = HandlerOf(nameof(EvmInstructions.InstructionMStore8), pops: 2, pushes: 0);
    private static readonly HandlerOp? s_keccak256 = HandlerOf(nameof(EvmInstructions.InstructionKeccak256), pops: 2, pushes: 1);
    private static readonly HandlerOp? s_callDataLoad = HandlerOf(nameof(EvmInstructions.InstructionCallDataLoad), pops: 1, pushes: 1);

    private static HandlerOp? HandlerOf(string name, int pops, int pushes)
    {
        try
        {
            MethodInfo? open = typeof(EvmInstructions).GetMethod(name);
            MethodInfo? closed = open?.MakeGenericMethod(typeof(EthereumGasPolicy), typeof(OffFlag));
            return closed is null ? null : new HandlerOp(closed, pops, pushes);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static HandlerOp? TryGetHandlerOp(Instruction instruction) => instruction switch
    {
        Instruction.MLOAD => s_mload,
        Instruction.MSTORE => s_mstore,
        Instruction.MSTORE8 => s_mstore8,
        Instruction.KECCAK256 => s_keccak256,
        Instruction.CALLDATALOAD => s_callDataLoad,
        _ => null,
    };

    public static bool TryCompile(ReadOnlySpan<byte> code, in BasicBlock block, out IlCompiledSegment? segment)
    {
        segment = null;
        if (!block.IsCompilable || s_gasConsume is null || s_popUInt256 is null
            || s_pushUInt256 is null || s_constantsValues is null)
        {
            return false;
        }

        List<DecodedOp> ops = DecodePrefix(code, in block, out int exitPc, out PrefixMetrics metrics);
        int exitPushes = metrics.StackRequired + metrics.StackFinalDelta;
        int boundaryTraffic = metrics.StackRequired + exitPushes + metrics.HandlerOperandTraffic;
        if (ops.Count < MinimumPrefixOps || ops.Count < BoundaryCostFactor * boundaryTraffic)
            return false;

        CompiledSegmentInvoke invoke = Emit(ops, in block, exitPc, in metrics);
        segment = new IlCompiledSegment
        {
            Invoke = invoke,
            EntryPc = block.Start,
            ExitPc = exitPc,
            OpCount = ops.Count,
            StaticGas = metrics.StaticGas,
            StackRequired = metrics.StackRequired,
            StackMaxGrowth = metrics.StackMaxDelta,
        };
        return true;
    }

    private readonly record struct DecodedOp(Instruction Instruction, OpInfo Info, UInt256 Constant, bool IsHandlerCall);

    private struct PrefixMetrics
    {
        public long StaticGas;
        public int StackRequired;
        public int StackMaxDelta;
        public int StackFinalDelta;
        public int HandlerOperandTraffic;
    }

    private static List<DecodedOp> DecodePrefix(ReadOnlySpan<byte> code, in BasicBlock block, out int exitPc, out PrefixMetrics metrics)
    {
        List<DecodedOp> ops = [];
        metrics = default;
        int delta = 0;
        int pc = block.Start;

        while (pc < block.End)
        {
            Instruction instruction = (Instruction)code[pc];
            bool isHandlerCall = false;
            if (!IsEmittable(instruction, out OpInfo info))
            {
                if (TryGetHandlerOp(instruction) is null || !TryGetHandlerOpInfo(instruction, out info))
                    break;
                isHandlerCall = true;
            }

            UInt256 constant = default;
            if (info.ImmediateBytes > 0)
            {
                int immediateStart = pc + 1;
                // Immediates running past the code end keep interpreter truncation semantics:
                // the prefix stops and the interpreter executes the truncated PUSH itself.
                if (immediateStart + info.ImmediateBytes > code.Length)
                    break;
                constant = new UInt256(code.Slice(immediateStart, info.ImmediateBytes), isBigEndian: true);
            }

            ops.Add(new DecodedOp(instruction, info, constant, isHandlerCall));
            // Handler statics belong to the gas PRECONDITION (the handler itself charges them
            // at execution); only the dynamic part can halt, exactly inside the handler.
            metrics.StaticGas += info.StaticGas;
            metrics.StackRequired = Math.Max(metrics.StackRequired, info.Pops - delta);
            delta += info.Pushes - info.Pops;
            metrics.StackMaxDelta = Math.Max(metrics.StackMaxDelta, delta);
            if (isHandlerCall)
                metrics.HandlerOperandTraffic += info.Pops + info.Pushes;

            pc += 1 + info.ImmediateBytes;
        }

        metrics.StackFinalDelta = delta;
        exitPc = pc;
        return ops;
    }

    /// <summary>Stack/gas metadata for the handler-call ops; all are fork-invariant.</summary>
    private static bool TryGetHandlerOpInfo(Instruction instruction, out OpInfo info)
    {
        switch (instruction)
        {
            case Instruction.MLOAD:
                info = new OpInfo(GasCostOf.VeryLow, Pops: 1, Pushes: 1, ImmediateBytes: 0, OpKind.Linear, HasDynamicGas: true);
                return true;
            case Instruction.MSTORE:
            case Instruction.MSTORE8:
                info = new OpInfo(GasCostOf.VeryLow, Pops: 2, Pushes: 0, ImmediateBytes: 0, OpKind.Linear, HasDynamicGas: true);
                return true;
            case Instruction.KECCAK256:
                info = new OpInfo(GasCostOf.Sha3, Pops: 2, Pushes: 1, ImmediateBytes: 0, OpKind.Linear, HasDynamicGas: true);
                return true;
            case Instruction.CALLDATALOAD:
                info = new OpInfo(GasCostOf.VeryLow, Pops: 1, Pushes: 1, ImmediateBytes: 0, OpKind.Linear);
                return true;
        }

        info = default;
        return false;
    }

    private static bool IsEmittable(Instruction instruction, out OpInfo info)
    {
        info = default;
        // Membership is intentionally spec-free: every op below is fork-invariant, and the
        // caller only feeds blocks the analyzer already classified compilable under its spec.
        switch (instruction)
        {
            case Instruction.JUMPDEST:
                // Emits no IL at all: its gas folds into the block's static charge.
                info = new OpInfo(GasCostOf.JumpDest, Pops: 0, Pushes: 0, ImmediateBytes: 0, OpKind.JumpDest);
                return true;
            case Instruction.POP:
                info = new OpInfo(GasCostOf.Base, Pops: 1, Pushes: 0, ImmediateBytes: 0, OpKind.Linear);
                return true;
            case Instruction.PUSH0:
                info = new OpInfo(GasCostOf.Base, Pops: 0, Pushes: 1, ImmediateBytes: 0, OpKind.Linear);
                return true;
            case >= Instruction.PUSH1 and <= Instruction.PUSH32:
                info = new OpInfo(GasCostOf.VeryLow, Pops: 0, Pushes: 1, ImmediateBytes: instruction - Instruction.PUSH1 + 1, OpKind.Linear);
                return true;
            case >= Instruction.DUP1 and <= Instruction.DUP16:
                {
                    int depth = instruction - Instruction.DUP1 + 1;
                    info = new OpInfo(GasCostOf.VeryLow, Pops: depth, Pushes: depth + 1, ImmediateBytes: 0, OpKind.Linear);
                    return true;
                }
            case >= Instruction.SWAP1 and <= Instruction.SWAP16:
                {
                    int depth = instruction - Instruction.SWAP1 + 2;
                    info = new OpInfo(GasCostOf.VeryLow, Pops: depth, Pushes: depth, ImmediateBytes: 0, OpKind.Linear);
                    return true;
                }
            case Instruction.ADD when s_add is not null:
            case Instruction.SUB when s_subtract is not null:
                info = new OpInfo(GasCostOf.VeryLow, Pops: 2, Pushes: 1, ImmediateBytes: 0, OpKind.Linear);
                return true;
            case Instruction.MUL when s_multiply is not null:
            case Instruction.DIV when s_divide is not null:
            case Instruction.SDIV when s_signedDivide is not null:
            case Instruction.MOD when s_mod is not null:
            case Instruction.SMOD when s_signedMod is not null:
            case Instruction.SIGNEXTEND when s_signExtend is not null:
                info = new OpInfo(GasCostOf.Low, Pops: 2, Pushes: 1, ImmediateBytes: 0, OpKind.Linear);
                return true;
            case Instruction.ADDMOD when s_addMod is not null:
            case Instruction.MULMOD when s_mulMod is not null:
                info = new OpInfo(GasCostOf.Mid, Pops: 3, Pushes: 1, ImmediateBytes: 0, OpKind.Linear);
                return true;
            case Instruction.SHL when s_shl is not null:
            case Instruction.SHR when s_shr is not null:
            case Instruction.SAR when s_sar is not null:
            case Instruction.BYTE when s_byte is not null:
            case Instruction.AND when s_opAnd is not null:
            case Instruction.OR when s_opOr is not null:
            case Instruction.XOR when s_opXor is not null:
            case Instruction.LT when s_lessThan is not null:
            case Instruction.GT when s_greaterThan is not null:
            case Instruction.SLT when s_signedLessThan is not null:
            case Instruction.SGT when s_signedGreaterThan is not null:
            case Instruction.EQ when s_opEquality is not null && s_fromUlong is not null:
                info = new OpInfo(GasCostOf.VeryLow, Pops: 2, Pushes: 1, ImmediateBytes: 0, OpKind.Linear);
                return true;
            case Instruction.ISZERO when s_isZero is not null && s_fromUlong is not null:
            case Instruction.NOT when s_not is not null:
                info = new OpInfo(GasCostOf.VeryLow, Pops: 1, Pushes: 1, ImmediateBytes: 0, OpKind.Linear);
                return true;
        }

        return false;
    }

    private enum OperandKind
    {
        Local,
        Constant,
    }

    private readonly record struct Operand(OperandKind Kind, LocalBuilder? Local, int ConstantIndex);

    private static CompiledSegmentInvoke Emit(List<DecodedOp> ops, in BasicBlock block, int exitPc, in PrefixMetrics metrics)
    {
        List<UInt256> constants = [];
        // The op count in the name reduces cross-contract collisions in profiler/stack traces
        // (names are cosmetic — DynamicMethods are identified by handle, not name).
        DynamicMethod method = new(
            $"IlEvmSegment_{block.Start}_{exitPc}_{ops.Count}",
            typeof(EvmExceptionType),
            [typeof(SegmentConstants), typeof(VirtualMachine<EthereumGasPolicy>), typeof(EvmStack).MakeByRefType(), typeof(EthereumGasPolicy).MakeByRefType(), typeof(int).MakeByRefType(), typeof(int)],
            typeof(IlSegmentCompiler).Module,
            skipVisibility: true);
        ILGenerator il = method.GetILGenerator();

        // The dispatch site has verified stack depth, stack headroom, and the segment's total
        // STATIC gas (see IlCompiledSegment preconditions). Static charges are emitted in
        // chunks so that at every embedded handler call the cumulative charge equals the
        // interpreter's — and only a handler's DYNAMIC charge (memory expansion, keccak words)
        // can halt, inside the handler, with exact accounting.
        // Entry pops, top first: e0 = top of stack on entry.
        List<Operand> symbolicStack = [];
        Operand[] entries = new Operand[metrics.StackRequired];
        for (int i = 0; i < metrics.StackRequired; i++)
        {
            LocalBuilder local = il.DeclareLocal(typeof(UInt256));
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Ldloca, local);
            il.Emit(OpCodes.Call, s_popUInt256!);
            il.Emit(OpCodes.Pop); // the upfront depth check guarantees success
            entries[i] = new Operand(OperandKind.Local, local, ConstantIndex: -1);
        }
        for (int i = metrics.StackRequired - 1; i >= 0; i--)
            symbolicStack.Add(entries[i]); // bottom .. top

        long pendingChunkGas = 0;
        foreach (DecodedOp op in ops)
        {
            if (op.IsHandlerCall)
            {
                EmitChargePendingGas(il, ref pendingChunkGas);
                EmitHandlerCall(il, op, symbolicStack);
            }
            else
            {
                pendingChunkGas += op.Info.StaticGas;
                EmitOp(il, op, symbolicStack, constants);
            }
        }
        EmitChargePendingGas(il, ref pendingChunkGas);

        // Push the surviving values back, bottom first; the upfront growth check guarantees success.
        foreach (Operand operand in symbolicStack)
        {
            il.Emit(OpCodes.Ldarg_2);
            EmitOperandAddress(il, operand);
            il.Emit(OpCodes.Call, s_pushUInt256!);
            il.Emit(OpCodes.Pop);
        }

        // programCounter = exitPc; return None;
        il.Emit(OpCodes.Ldarg_S, (byte)4);
        il.Emit(OpCodes.Ldc_I4, exitPc);
        il.Emit(OpCodes.Stind_I4);
        il.Emit(OpCodes.Ldc_I4, (int)EvmExceptionType.None);
        il.Emit(OpCodes.Ret);

        SegmentConstants segmentConstants = new([.. constants]);
        return (CompiledSegmentInvoke)method.CreateDelegate(typeof(CompiledSegmentInvoke), segmentConstants);
    }

    private static void EmitChargePendingGas(ILGenerator il, ref long pendingChunkGas)
    {
        if (pendingChunkGas == 0)
            return;
        il.Emit(OpCodes.Ldarg_3);
        il.Emit(OpCodes.Ldc_I8, pendingChunkGas);
        il.Emit(OpCodes.Call, s_gasConsume!);
        pendingChunkGas = 0;
    }

    /// <summary>
    /// Executes one opcode by calling the interpreter's own handler mid-segment: flushes just
    /// the op's operands to the real stack (top of the operand group last, so it is the real
    /// top), calls the handler — which charges its own static and dynamic gas and may halt —
    /// and reloads any result into a fresh local. A non-None handler result is returned to the
    /// dispatch loop unchanged, exactly as if the handler had been interpreted.
    /// </summary>
    private static void EmitHandlerCall(ILGenerator il, in DecodedOp op, List<Operand> symbolicStack)
    {
        HandlerOp handler = TryGetHandlerOp(op.Instruction)!.Value;

        int operandStart = symbolicStack.Count - handler.Pops;
        for (int i = operandStart; i < symbolicStack.Count; i++)
        {
            il.Emit(OpCodes.Ldarg_2);
            EmitOperandAddress(il, symbolicStack[i]);
            il.Emit(OpCodes.Call, s_pushUInt256!);
            il.Emit(OpCodes.Pop); // headroom is covered by the upfront growth precondition
        }
        symbolicStack.RemoveRange(operandStart, handler.Pops);

        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Ldarg_3);
        il.Emit(OpCodes.Ldarg_S, (byte)4);
        il.Emit(OpCodes.Call, handler.Method);
        Label success = il.DefineLabel();
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4, (int)EvmExceptionType.None);
        il.Emit(OpCodes.Beq, success);
        il.Emit(OpCodes.Ret); // propagate the handler's halt; the frame is dead, locals are moot
        il.MarkLabel(success);
        il.Emit(OpCodes.Pop);

        for (int i = 0; i < handler.Pushes; i++)
        {
            LocalBuilder result = il.DeclareLocal(typeof(UInt256));
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Ldloca, result);
            il.Emit(OpCodes.Call, s_popUInt256!);
            il.Emit(OpCodes.Pop);
            symbolicStack.Add(new Operand(OperandKind.Local, result, ConstantIndex: -1));
        }
    }

    private static void EmitOp(ILGenerator il, in DecodedOp op, List<Operand> symbolicStack, List<UInt256> constants)
    {
        switch (op.Instruction)
        {
            case Instruction.POP:
                symbolicStack.RemoveAt(symbolicStack.Count - 1);
                break;

            case Instruction.JUMPDEST:
                break;

            case Instruction.PUSH0:
            case >= Instruction.PUSH1 and <= Instruction.PUSH32:
                constants.Add(op.Constant);
                symbolicStack.Add(new Operand(OperandKind.Constant, Local: null, constants.Count - 1));
                break;

            case >= Instruction.DUP1 and <= Instruction.DUP16:
                {
                    int depth = op.Instruction - Instruction.DUP1 + 1;
                    symbolicStack.Add(symbolicStack[^depth]);
                    break;
                }

            case >= Instruction.SWAP1 and <= Instruction.SWAP16:
                {
                    int depth = op.Instruction - Instruction.SWAP1 + 2;
                    (symbolicStack[^1], symbolicStack[^depth]) = (symbolicStack[^depth], symbolicStack[^1]);
                    break;
                }

            case Instruction.ADD:
                EmitBinaryWithOut(il, symbolicStack, s_add!);
                break;
            case Instruction.SUB:
                EmitBinaryWithOut(il, symbolicStack, s_subtract!);
                break;
            case Instruction.MUL:
                EmitBinaryWithOut(il, symbolicStack, s_multiply!);
                break;
            case Instruction.DIV:
                EmitBinaryWithOut(il, symbolicStack, s_divide!);
                break;
            case Instruction.SDIV:
                EmitBinaryWithOut(il, symbolicStack, s_signedDivide!);
                break;
            case Instruction.MOD:
                EmitBinaryWithOut(il, symbolicStack, s_mod!);
                break;
            case Instruction.SMOD:
                EmitBinaryWithOut(il, symbolicStack, s_signedMod!);
                break;
            case Instruction.SIGNEXTEND:
                EmitBinaryWithOut(il, symbolicStack, s_signExtend!);
                break;
            case Instruction.SHL:
                EmitBinaryWithOut(il, symbolicStack, s_shl!);
                break;
            case Instruction.SHR:
                EmitBinaryWithOut(il, symbolicStack, s_shr!);
                break;
            case Instruction.SAR:
                EmitBinaryWithOut(il, symbolicStack, s_sar!);
                break;
            case Instruction.BYTE:
                EmitBinaryWithOut(il, symbolicStack, s_byte!);
                break;
            case Instruction.LT:
                EmitBinaryWithOut(il, symbolicStack, s_lessThan!);
                break;
            case Instruction.GT:
                EmitBinaryWithOut(il, symbolicStack, s_greaterThan!);
                break;
            case Instruction.SLT:
                EmitBinaryWithOut(il, symbolicStack, s_signedLessThan!);
                break;
            case Instruction.SGT:
                EmitBinaryWithOut(il, symbolicStack, s_signedGreaterThan!);
                break;

            case Instruction.ADDMOD:
                EmitTernaryWithOut(il, symbolicStack, s_addMod!);
                break;
            case Instruction.MULMOD:
                EmitTernaryWithOut(il, symbolicStack, s_mulMod!);
                break;

            case Instruction.AND:
                EmitBinaryReturningValue(il, symbolicStack, s_opAnd!);
                break;
            case Instruction.OR:
                EmitBinaryReturningValue(il, symbolicStack, s_opOr!);
                break;
            case Instruction.XOR:
                EmitBinaryReturningValue(il, symbolicStack, s_opXor!);
                break;

            case Instruction.EQ:
                EmitComparison(il, symbolicStack, s_opEquality!);
                break;

            case Instruction.NOT:
                EmitUnaryWithOut(il, symbolicStack, s_not!);
                break;

            case Instruction.ISZERO:
                {
                    Operand a = PopSymbolic(symbolicStack);
                    EmitOperandAddress(il, a);
                    il.Emit(OpCodes.Call, s_isZero!);
                    EmitBoolToUInt256(il, symbolicStack);
                    break;
                }

            default:
                throw new InvalidOperationException($"Opcode {op.Instruction} decoded as emittable but has no emitter");
        }
    }

    /// <summary>EVM binary op order: a = top, b = second; result = op(a, b), matching Math2Param.</summary>
    private static void EmitBinaryWithOut(ILGenerator il, List<Operand> symbolicStack, MethodInfo method)
    {
        Operand a = PopSymbolic(symbolicStack);
        Operand b = PopSymbolic(symbolicStack);
        LocalBuilder result = il.DeclareLocal(typeof(UInt256));
        EmitOperandAddress(il, a);
        EmitOperandAddress(il, b);
        il.Emit(OpCodes.Ldloca, result);
        il.Emit(OpCodes.Call, method);
        symbolicStack.Add(new Operand(OperandKind.Local, result, ConstantIndex: -1));
    }

    /// <summary>EVM ternary op order: a = top, b = second, c = third, matching Math3Param.</summary>
    private static void EmitTernaryWithOut(ILGenerator il, List<Operand> symbolicStack, MethodInfo method)
    {
        Operand a = PopSymbolic(symbolicStack);
        Operand b = PopSymbolic(symbolicStack);
        Operand c = PopSymbolic(symbolicStack);
        LocalBuilder result = il.DeclareLocal(typeof(UInt256));
        EmitOperandAddress(il, a);
        EmitOperandAddress(il, b);
        EmitOperandAddress(il, c);
        il.Emit(OpCodes.Ldloca, result);
        il.Emit(OpCodes.Call, method);
        symbolicStack.Add(new Operand(OperandKind.Local, result, ConstantIndex: -1));
    }

    private static void EmitUnaryWithOut(ILGenerator il, List<Operand> symbolicStack, MethodInfo method)
    {
        Operand a = PopSymbolic(symbolicStack);
        LocalBuilder result = il.DeclareLocal(typeof(UInt256));
        EmitOperandAddress(il, a);
        il.Emit(OpCodes.Ldloca, result);
        il.Emit(OpCodes.Call, method);
        symbolicStack.Add(new Operand(OperandKind.Local, result, ConstantIndex: -1));
    }

    private static void EmitBinaryReturningValue(ILGenerator il, List<Operand> symbolicStack, MethodInfo method)
    {
        Operand a = PopSymbolic(symbolicStack);
        Operand b = PopSymbolic(symbolicStack);
        LocalBuilder result = il.DeclareLocal(typeof(UInt256));
        EmitOperandForParameter(il, a, method, parameterIndex: 0);
        EmitOperandForParameter(il, b, method, parameterIndex: 1);
        il.Emit(OpCodes.Call, method);
        il.Emit(OpCodes.Stloc, result);
        symbolicStack.Add(new Operand(OperandKind.Local, result, ConstantIndex: -1));
    }

    private static void EmitComparison(ILGenerator il, List<Operand> symbolicStack, MethodInfo comparison)
    {
        Operand a = PopSymbolic(symbolicStack);
        Operand b = PopSymbolic(symbolicStack);
        EmitOperandForParameter(il, a, comparison, parameterIndex: 0);
        EmitOperandForParameter(il, b, comparison, parameterIndex: 1);
        il.Emit(OpCodes.Call, comparison);
        EmitBoolToUInt256(il, symbolicStack);
    }

    /// <summary>Operators may declare their operands either by-ref (in) or by-value.</summary>
    private static void EmitOperandForParameter(ILGenerator il, in Operand operand, MethodInfo method, int parameterIndex)
    {
        EmitOperandAddress(il, operand);
        if (!method.GetParameters()[parameterIndex].ParameterType.IsByRef)
            il.Emit(OpCodes.Ldobj, typeof(UInt256));
    }

    private static void EmitBoolToUInt256(ILGenerator il, List<Operand> symbolicStack)
    {
        LocalBuilder result = il.DeclareLocal(typeof(UInt256));
        il.Emit(OpCodes.Conv_U8);
        il.Emit(OpCodes.Call, s_fromUlong!);
        il.Emit(OpCodes.Stloc, result);
        symbolicStack.Add(new Operand(OperandKind.Local, result, ConstantIndex: -1));
    }

    private static Operand PopSymbolic(List<Operand> symbolicStack)
    {
        Operand top = symbolicStack[^1];
        symbolicStack.RemoveAt(symbolicStack.Count - 1);
        return top;
    }

    private static void EmitOperandAddress(ILGenerator il, in Operand operand)
    {
        if (operand.Kind == OperandKind.Local)
        {
            il.Emit(OpCodes.Ldloca, operand.Local!);
        }
        else
        {
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, s_constantsValues!);
            il.Emit(OpCodes.Ldc_I4, operand.ConstantIndex);
            il.Emit(OpCodes.Ldelema, typeof(UInt256));
        }
    }

    private static MethodInfo? OperationOf(Type opStruct) => opStruct.GetMethod("Operation");

    private static MethodInfo? IntrinsicOf(string name) => typeof(IlEvmIntrinsics).GetMethod(name);

    private static MethodInfo? RequireVoid(MethodInfo? method) => method?.ReturnType == typeof(void) ? method : null;

    private static MethodInfo? BinaryOperator(string name) =>
        typeof(UInt256).GetMethod(name, [typeof(UInt256).MakeByRefType(), typeof(UInt256).MakeByRefType()])
        ?? typeof(UInt256).GetMethod(name, [typeof(UInt256), typeof(UInt256)]);

    private static MethodInfo? ImplicitFromUlong()
    {
        foreach (MethodInfo method in typeof(UInt256).GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (method.Name == "op_Implicit" && method.ReturnType == typeof(UInt256))
            {
                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length == 1 && parameters[0].ParameterType == typeof(ulong))
                    return method;
            }
        }

        return null;
    }
}

/// <summary>
/// Holds the PUSH immediates of one compiled segment; bound as the delegate target.
/// Immutable after construction — compiled segments are shared across threads.
/// </summary>
public sealed class SegmentConstants(UInt256[] values)
{
    // A field, not a property: the emitted IL reads it with ldfld (skipVisibility grants access).
    private readonly UInt256[] _values = values;

    internal const string ValuesFieldName = nameof(_values);
}
