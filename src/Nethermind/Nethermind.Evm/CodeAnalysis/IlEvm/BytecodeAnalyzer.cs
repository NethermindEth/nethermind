// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Specs;

namespace Nethermind.Evm.CodeAnalysis.IlEvm;

/// <summary>
/// The IL-EVM analysis front-end: cuts legacy bytecode into <see cref="BasicBlock"/>s with
/// per-block static gas, stack effects, and a compilable/interpreter-only classification.
///
/// Leaders are positions reached by a control-flow edge: position 0, every JUMPDEST, and the
/// position after a JUMP/JUMPI/terminator. Bytes inside PUSH immediates are data, never
/// opcodes, and are skipped exactly as the interpreter's program counter skips them — a 0x5B
/// byte inside push data is not a leader (matching <see cref="JumpDestinationAnalyzer"/>).
///
/// Blocks are additionally cut where the opcode classification flips between the v1 subset and
/// interpreter-only opcodes, so every compilable block is valid segment-compiler input in full.
/// </summary>
public static class BytecodeAnalyzer
{
    public static AnalyzedCode Analyze(ReadOnlySpan<byte> code, IReleaseSpec spec)
    {
        if (code.Length == 0)
            return AnalyzedCode.Empty;

        // The IsEip8024Enabled spec flag (see IReleaseSpec; gates DUPN/SWAPN/EXCHANGE in
        // EvmInstructions.GenerateOpCodes) introduces non-PUSH immediates. The scanner assumes
        // only PUSH carries immediates, so analysis is disabled on such specs until supported.
        if (spec.IsEip8024Enabled)
            return AnalyzedCode.Empty;

        bool[] isLeader = new bool[code.Length];
        MarkLeaders(code, isLeader);
        return CutBlocks(code, spec, isLeader);
    }

    private static void MarkLeaders(ReadOnlySpan<byte> code, bool[] isLeader)
    {
        isLeader[0] = true;
        int pc = 0;
        while (pc < code.Length)
        {
            Instruction instruction = (Instruction)code[pc];
            if (instruction == Instruction.JUMPDEST)
                isLeader[pc] = true;

            int next = pc + 1 + PushImmediateBytes(instruction);
            if (EndsLinearFlow(instruction) && next < code.Length)
                isLeader[next] = true;

            pc = next;
        }
    }

    private static AnalyzedCode CutBlocks(ReadOnlySpan<byte> code, IReleaseSpec spec, bool[] isLeader)
    {
        List<BasicBlock> blocks = [];
        int[] blockIndexByPc = new int[code.Length];
        Array.Fill(blockIndexByPc, -1);

        int blockStart = 0;
        bool blockOpen = false;
        bool blockCompilable = false;
        long staticGas = 0;
        int stackRequired = 0;
        int stackMaxGrowth = 0;
        int stackDelta = 0;
        BasicBlockFlags flags = BasicBlockFlags.None;

        void OpenBlock(int start, bool compilable, bool startsWithJumpDest)
        {
            blockStart = start;
            blockOpen = true;
            blockCompilable = compilable;
            staticGas = 0;
            stackRequired = 0;
            stackMaxGrowth = 0;
            stackDelta = 0;
            flags = compilable ? BasicBlockFlags.Compilable : BasicBlockFlags.None;
            if (startsWithJumpDest)
                flags |= BasicBlockFlags.StartsWithJumpDest;
            blockIndexByPc[start] = blocks.Count;
        }

        void CloseBlock(int end)
        {
            blocks.Add(blockCompilable
                ? new BasicBlock(blockStart, end, staticGas, stackRequired, stackMaxGrowth, stackDelta, flags)
                : new BasicBlock(blockStart, end, staticGas: 0, stackRequired: 0, stackMaxGrowth: 0, stackDelta: 0, flags));
            blockOpen = false;
        }

        int pc = 0;
        while (pc < code.Length)
        {
            Instruction instruction = (Instruction)code[pc];
            bool isV1 = IlEvmOpcodes.TryGetV1(instruction, spec, out OpInfo info);

            if (blockOpen && (isLeader[pc] || isV1 != blockCompilable))
                CloseBlock(pc);

            if (!blockOpen)
                OpenBlock(pc, isV1, instruction == Instruction.JUMPDEST);

            bool blockEnds = false;
            if (isV1)
            {
                staticGas += info.StaticGas;
                stackRequired = Math.Max(stackRequired, info.Pops - stackDelta);
                stackDelta += info.Pushes - info.Pops;
                stackMaxGrowth = Math.Max(stackMaxGrowth, stackDelta);
                if (info.HasDynamicGas)
                    flags |= BasicBlockFlags.HasDynamicGas;

                switch (info.Kind)
                {
                    case OpKind.Jump:
                        flags |= BasicBlockFlags.EndsWithJump;
                        blockEnds = true;
                        break;
                    case OpKind.ConditionalJump:
                        flags |= BasicBlockFlags.EndsWithConditionalJump;
                        blockEnds = true;
                        break;
                    case OpKind.Terminator:
                        flags |= BasicBlockFlags.EndsWithTerminator;
                        blockEnds = true;
                        break;
                }
            }
            else if (IlEvmOpcodes.IsNonV1Terminator(instruction))
            {
                flags |= BasicBlockFlags.EndsWithTerminator;
                blockEnds = true;
            }

            int next = pc + 1 + (isV1 ? info.ImmediateBytes : 0);
            if (next > code.Length)
                next = code.Length;
            pc = next;

            if (blockEnds)
                CloseBlock(pc);
        }

        if (blockOpen)
            CloseBlock(code.Length);

        return new AnalyzedCode([.. blocks], blockIndexByPc);
    }

    private static int PushImmediateBytes(Instruction instruction) =>
        instruction is >= Instruction.PUSH1 and <= Instruction.PUSH32
            ? instruction - Instruction.PUSH1 + 1
            : 0;

    private static bool EndsLinearFlow(Instruction instruction) =>
        instruction is Instruction.JUMP or Instruction.JUMPI or Instruction.STOP
        || IlEvmOpcodes.IsNonV1Terminator(instruction);
}
