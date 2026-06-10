// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using Nethermind.Core;
using Nethermind.Evm.GasPolicy;
using Nethermind.Int256;

namespace Nethermind.Evm.CodeAnalysis.IlEvm;

/// <summary>
/// v4 region compilation: a REGION is a maximal run of consecutive compilable blocks compiled
/// into ONE DynamicMethod, so that terminal JUMP/JUMPI with compile-time-constant destinations
/// become native IL branches — whole loops execute without returning to the interpreter.
///
/// Correctness model (no refunds, no native halts for static failures):
/// - The real EVM stack is the canonical state at every block boundary (each block pops its
///   entries, computes in locals, flushes survivors), so control-flow merge points need no
///   value tracking across edges.
/// - Every block begins with prechecks — remaining gas ≥ the block's total static gas (checked
///   WITHOUT charging) and stack depth/headroom. On failure the region sets pc to the block
///   start and returns None: the dispatch loop falls through to the interpreter, which replays
///   the block per-op and produces the exact halt (this is how loops run out of gas with
///   to-the-unit accounting).
/// - Within a block, static gas is charged in chunks and memory/keccak run as embedded handler
///   calls exactly as in v3; handler dynamic-gas halts propagate unchanged.
/// - Constant jump destinations are validated at compile time against the analyzer's leaders:
///   in-region valid → IL branch; out-of-region valid → pc = dest, return None; invalid →
///   return InvalidJumpDestination (the jump's gas has been charged, matching the interpreter).
///   Dynamic destinations cut the block: pc = the jump's pc, the interpreter executes it.
/// </summary>
public static partial class IlSegmentCompiler
{
    /// <summary>
    /// Compiles the region starting at <paramref name="firstBlockIndex"/> and registers one
    /// segment per externally reachable entry into <paramref name="segmentsByBlockIndex"/>.
    /// Returns the number of consecutive blocks consumed (at least 1), whether compiled or not.
    /// </summary>
    public static int TryCompileRegion(ReadOnlySpan<byte> code, AnalyzedCode analyzed, int firstBlockIndex, IlCompiledSegment?[] segmentsByBlockIndex, out int segmentCount)
    {
        segmentCount = 0;
        ReadOnlySpan<BasicBlock> blocks = analyzed.Blocks;
        if (!blocks[firstBlockIndex].IsCompilable || s_gasConsume is null || s_gasRemaining is null
            || s_popUInt256 is null || s_pushUInt256 is null || s_constantsValues is null || s_stackHead is null || s_isZero is null)
        {
            return 1;
        }

        List<RegionBlock> region = CollectRegion(code, analyzed, firstBlockIndex);
        if (region.Count == 0)
            return 1;

        int totalOps = 0;
        int totalBoundaryTraffic = 0;
        foreach (RegionBlock regionBlock in region)
        {
            totalOps += regionBlock.Ops.Count;
            totalBoundaryTraffic += regionBlock.Metrics.StackRequired
                + Math.Max(0, regionBlock.Metrics.StackRequired + regionBlock.Metrics.StackFinalDelta)
                + regionBlock.Metrics.HandlerOperandTraffic;
        }
        if (totalOps < MinimumPrefixOps || totalOps < BoundaryCostFactor * totalBoundaryTraffic)
            return region.Count;

        CompiledSegmentInvoke invoke;
        try
        {
            invoke = EmitRegion(analyzed, region, out List<UInt256> _);
        }
        catch (Exception)
        {
            return region.Count; // emission failure → interpreter keeps the region
        }

        int entryOrdinal = 0;
        foreach (RegionBlock regionBlock in region)
        {
            if (!regionBlock.IsEntry)
                continue;
            segmentsByBlockIndex[regionBlock.BlockIndex] = new IlCompiledSegment
            {
                Invoke = invoke,
                EntryPc = regionBlock.Block.Start,
                ExitPc = regionBlock.CutPc,
                OpCount = regionBlock.Ops.Count,
                StaticGas = regionBlock.Metrics.StaticGas,
                StackRequired = regionBlock.Metrics.StackRequired,
                StackMaxGrowth = Math.Max(0, regionBlock.Metrics.StackMaxDelta),
                EntryIndex = entryOrdinal,
            };
            entryOrdinal++;
            segmentCount++;
        }

        return region.Count;
    }

    private sealed class RegionBlock
    {
        public BasicBlock Block;
        public int BlockIndex;
        public required List<DecodedOp> Ops;
        public PrefixMetrics Metrics;
        public int CutPc;
        public bool IsFullyEmittable;          // prefix covers the block up to its end or terminal jump
        public Instruction TerminalJump;       // JUMP or JUMPI when a constant-destination jump terminates the block
        public bool HasConstantJump;
        public int JumpDestination;
        public bool IsEntry;
        public Label Label;
    }

    private static List<RegionBlock> CollectRegion(ReadOnlySpan<byte> code, AnalyzedCode analyzed, int firstBlockIndex)
    {
        List<RegionBlock> region = [];
        ReadOnlySpan<BasicBlock> blocks = analyzed.Blocks;

        for (int blockIndex = firstBlockIndex; blockIndex < blocks.Length; blockIndex++)
        {
            BasicBlock block = blocks[blockIndex];
            if (!block.IsCompilable)
                break;
            if (region.Count > 0 && region[^1].Block.End != block.Start)
                break; // non-contiguous (defensive; analyzer blocks tile the code)

            List<DecodedOp> ops = DecodePrefix(code, in block, out int cutPc, out PrefixMetrics metrics);

            RegionBlock regionBlock = new()
            {
                Block = block,
                BlockIndex = blockIndex,
                Ops = ops,
                Metrics = metrics,
                CutPc = cutPc,
                IsFullyEmittable = cutPc == block.End,
            };

            // A terminal JUMP/JUMPI directly preceded by its destination PUSH compiles to a
            // native branch: drop the PUSH from emission, keep its gas and stack effect in the
            // block's metrics (push-then-pop cancels; JUMPI's condition pop is added below).
            if (cutPc == block.End - 1 && ops.Count > 0
                && (Instruction)code[cutPc] is Instruction.JUMP or Instruction.JUMPI
                && ops[^1].Instruction is >= Instruction.PUSH1 and <= Instruction.PUSH32 or Instruction.PUSH0)
            {
                Instruction jump = (Instruction)code[cutPc];
                UInt256 destination = ops[^1].Constant;
                if (destination.IsUint64 && destination.u0 <= int.MaxValue)
                {
                    regionBlock.TerminalJump = jump;
                    regionBlock.HasConstantJump = true;
                    regionBlock.JumpDestination = (int)destination.u0;
                    regionBlock.IsFullyEmittable = true;
                    ops.RemoveAt(ops.Count - 1);

                    // Metrics: the removed PUSH already contributed (+1 delta, its gas); the
                    // jump pops it back plus, for JUMPI, the condition beneath it.
                    int jumpPops = jump == Instruction.JUMP ? 1 : 2;
                    regionBlock.Metrics.StackRequired = Math.Max(regionBlock.Metrics.StackRequired, jumpPops - regionBlock.Metrics.StackFinalDelta);
                    regionBlock.Metrics.StackFinalDelta -= jumpPops;
                    regionBlock.Metrics.StaticGas += jump == Instruction.JUMP ? GasCostOf.Mid : GasCostOf.High;
                }
            }

            if (ops.Count == 0 && !regionBlock.HasConstantJump)
            {
                // Nothing emittable (e.g. a lone dynamic JUMP): leave the block to the
                // interpreter; it would otherwise be an empty entry.
                if (region.Count == 0)
                    return region;
                break;
            }

            regionBlock.IsEntry = region.Count == 0
                || (block.Flags & BasicBlockFlags.StartsWithJumpDest) != 0
                || !region[^1].IsFullyEmittable; // continuation after a cut: re-enterable via dispatch
            region.Add(regionBlock);
        }

        return region;
    }

    private static CompiledSegmentInvoke EmitRegion(AnalyzedCode analyzed, List<RegionBlock> region, out List<UInt256> constants)
    {
        constants = [];
        DynamicMethod method = new(
            $"IlEvmRegion_{region[0].Block.Start}_{region[^1].Block.End}_{region.Count}",
            typeof(EvmExceptionType),
            [typeof(SegmentConstants), typeof(VirtualMachine<EthereumGasPolicy>), typeof(EvmStack).MakeByRefType(), typeof(EthereumGasPolicy).MakeByRefType(), typeof(int).MakeByRefType(), typeof(int)],
            typeof(IlSegmentCompiler).Module,
            skipVisibility: true);
        ILGenerator il = method.GetILGenerator();

        foreach (RegionBlock regionBlock in region)
            regionBlock.Label = il.DefineLabel();

        // Entry dispatch: switch over the dense entry ordinal passed by the dispatch site.
        List<Label> entryLabels = [];
        foreach (RegionBlock regionBlock in region)
        {
            if (regionBlock.IsEntry)
                entryLabels.Add(regionBlock.Label);
        }
        il.Emit(OpCodes.Ldarg_S, (byte)5);
        il.Emit(OpCodes.Switch, entryLabels.ToArray());
        il.Emit(OpCodes.Ldc_I4, (int)EvmExceptionType.None); // unreachable defensive default
        il.Emit(OpCodes.Ret);

        for (int i = 0; i < region.Count; i++)
            EmitRegionBlock(il, analyzed, region, i, constants);

        SegmentConstants segmentConstants = new([.. constants]);
        return (CompiledSegmentInvoke)method.CreateDelegate(typeof(CompiledSegmentInvoke), segmentConstants);
    }

    private static void EmitRegionBlock(ILGenerator il, AnalyzedCode analyzed, List<RegionBlock> region, int index, List<UInt256> constants)
    {
        RegionBlock regionBlock = region[index];
        il.MarkLabel(regionBlock.Label);
        Label bail = il.DefineLabel();

        // Gas precheck WITHOUT charging: a failing block is replayed per-op by the interpreter.
        if (regionBlock.Metrics.StaticGas > 0)
        {
            il.Emit(OpCodes.Ldarg_3);
            il.Emit(OpCodes.Call, s_gasRemaining!);
            il.Emit(OpCodes.Ldc_I8, regionBlock.Metrics.StaticGas);
            il.Emit(OpCodes.Blt, bail);
        }
        if (regionBlock.Metrics.StackRequired > 0)
        {
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Ldfld, s_stackHead!);
            il.Emit(OpCodes.Ldc_I4, regionBlock.Metrics.StackRequired);
            il.Emit(OpCodes.Blt, bail);
        }
        if (regionBlock.Metrics.StackMaxDelta > 0)
        {
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Ldfld, s_stackHead!);
            il.Emit(OpCodes.Ldc_I4, UsableStackSlots - regionBlock.Metrics.StackMaxDelta);
            il.Emit(OpCodes.Bgt, bail);
        }

        // Entry pops, top first (e0 = top).
        List<Operand> symbolicStack = [];
        Operand[] entries = new Operand[regionBlock.Metrics.StackRequired];
        for (int i = 0; i < entries.Length; i++)
        {
            LocalBuilder local = il.DeclareLocal(typeof(UInt256));
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Ldloca, local);
            il.Emit(OpCodes.Call, s_popUInt256!);
            il.Emit(OpCodes.Pop);
            entries[i] = new Operand(OperandKind.Local, local, ConstantIndex: -1);
        }
        for (int i = entries.Length - 1; i >= 0; i--)
            symbolicStack.Add(entries[i]);

        long pendingChunkGas = 0;
        foreach (DecodedOp op in regionBlock.Ops)
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

        if (regionBlock.HasConstantJump)
        {
            // Charge the omitted destination-PUSH and the jump itself, mirroring interpretation.
            pendingChunkGas += GasCostOf.VeryLow;
            pendingChunkGas += regionBlock.TerminalJump == Instruction.JUMP ? GasCostOf.Mid : GasCostOf.High;
            EmitChargePendingGas(il, ref pendingChunkGas);

            Operand condition = default;
            if (regionBlock.TerminalJump == Instruction.JUMPI)
                condition = PopSymbolic(symbolicStack);

            FlushSymbolicStack(il, symbolicStack);

            if (regionBlock.TerminalJump == Instruction.JUMP)
            {
                EmitJumpToDestination(il, analyzed, region, regionBlock.JumpDestination);
            }
            else
            {
                Label notTaken = il.DefineLabel();
                EmitOperandAddress(il, condition);
                il.Emit(OpCodes.Call, s_isZero!);
                il.Emit(OpCodes.Brtrue, notTaken);
                EmitJumpToDestination(il, analyzed, region, regionBlock.JumpDestination);
                il.MarkLabel(notTaken);
                EmitContinueAfterBlock(il, region, index, regionBlock.Block.End);
            }
        }
        else
        {
            EmitChargePendingGas(il, ref pendingChunkGas);
            FlushSymbolicStack(il, symbolicStack);
            if (regionBlock.IsFullyEmittable)
                EmitContinueAfterBlock(il, region, index, regionBlock.Block.End);
            else
                EmitExitWithPc(il, regionBlock.CutPc); // cut: the interpreter resumes here
        }

        il.MarkLabel(bail);
        EmitExitWithPc(il, regionBlock.Block.Start);
    }

    private static void FlushSymbolicStack(ILGenerator il, List<Operand> symbolicStack)
    {
        foreach (Operand operand in symbolicStack)
        {
            il.Emit(OpCodes.Ldarg_2);
            EmitOperandAddress(il, operand);
            il.Emit(OpCodes.Call, s_pushUInt256!);
            il.Emit(OpCodes.Pop);
        }
        symbolicStack.Clear();
    }

    /// <summary>Jump with a compile-time destination: in-region label, valid external target, or invalid-destination halt.</summary>
    private static void EmitJumpToDestination(ILGenerator il, AnalyzedCode analyzed, List<RegionBlock> region, int destination)
    {
        foreach (RegionBlock candidate in region)
        {
            if (candidate.Block.Start == destination && (candidate.Block.Flags & BasicBlockFlags.StartsWithJumpDest) != 0)
            {
                il.Emit(OpCodes.Br, candidate.Label);
                return;
            }
        }

        if (analyzed.TryGetBlockStartingAt(destination, out BasicBlock target)
            && (target.Flags & BasicBlockFlags.StartsWithJumpDest) != 0)
        {
            EmitExitWithPc(il, destination); // valid but outside the region: dispatch/interpreter takes over
            return;
        }

        il.Emit(OpCodes.Ldc_I4, (int)EvmExceptionType.InvalidJumpDestination);
        il.Emit(OpCodes.Ret);
    }

    private static void EmitContinueAfterBlock(ILGenerator il, List<RegionBlock> region, int index, int fallthroughPc)
    {
        if (index + 1 < region.Count && region[index + 1].Block.Start == fallthroughPc)
        {
            il.Emit(OpCodes.Br, region[index + 1].Label);
            return;
        }

        EmitExitWithPc(il, fallthroughPc);
    }

    private static void EmitExitWithPc(ILGenerator il, int programCounter)
    {
        il.Emit(OpCodes.Ldarg_S, (byte)4);
        il.Emit(OpCodes.Ldc_I4, programCounter);
        il.Emit(OpCodes.Stind_I4);
        il.Emit(OpCodes.Ldc_I4, (int)EvmExceptionType.None);
        il.Emit(OpCodes.Ret);
    }
}
