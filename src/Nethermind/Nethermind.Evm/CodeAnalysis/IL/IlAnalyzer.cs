// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;
using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using static Nethermind.Evm.CodeAnalysis.IL.ILCompiler;
using static System.Net.Mime.MediaTypeNames;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace Nethermind.Evm.CodeAnalysis.IL;

/// <summary>
/// Provides
/// </summary>
internal static class IlAnalyzer
{
    public class ByteArrayComparer : IEqualityComparer<byte[]>
    {
        public bool Equals(byte[] left, byte[] right)
        {
            if (left == null || right == null)
            {
                return left == right;
            }
            return left.SequenceEqual(right);
        }
        public int GetHashCode(byte[] key)
        {
            if (key == null)
                throw new ArgumentNullException("key");
            return key.Sum(b => b);
        }
    }

    private static Dictionary<byte[], InstructionChunk> Patterns = new Dictionary<byte[], InstructionChunk>(new ByteArrayComparer());
    public static Dictionary<byte[], InstructionChunk> AddPattern(byte[] pattern, InstructionChunk chunk)
    {
        lock (Patterns)
        {
            Patterns[pattern] = chunk;
        }
        return Patterns;
    }
    public static T GetPatternHandler<T>(byte[] pattern) where T : InstructionChunk
    {
        return (T)Patterns[pattern];
    }


    /// <summary>
    /// Starts the analyzing in a background task and outputs the value in the <paramref name="codeInfo"/>.
    /// </summary> thou
    /// <param name="codeInfo">The destination output.</param>
    public static Task StartAnalysis(CodeInfo codeInfo, IlInfo.ILMode mode)
    {
        return Task.Run(() => Analysis(codeInfo, mode));
    }

    public static (OpcodeInfo[], byte[][]) StripByteCode(ReadOnlySpan<byte> machineCode)
    {
        OpcodeInfo[] opcodes = new OpcodeInfo[machineCode.Length];
        List<byte[]> data = new List<byte[]>();
        int j = 0;
        for (ushort i = 0; i < machineCode.Length; i++, j++)
        {
            Instruction opcode = (Instruction)machineCode[i];
            int? argsIndex = null;
            ushort pc = i;
            if (opcode is > Instruction.PUSH0 and <= Instruction.PUSH32)
            {
                ushort immediatesCount = opcode - Instruction.PUSH0;
                data.Add(machineCode.SliceWithZeroPadding((UInt256)i + 1, immediatesCount, PadDirection.Left).ToArray());
                argsIndex = data.Count - 1;
                i += immediatesCount;
            }
            opcodes[j] = new OpcodeInfo(pc, opcode, argsIndex);
        }
        return (opcodes[..j], data.ToArray());
    }

    /// <summary>
    /// For now, return null always to default to EVM.
    /// </summary>
    private static void Analysis(CodeInfo codeInfo, IlInfo.ILMode mode)
    {
        ReadOnlyMemory<byte> machineCode = codeInfo.MachineCode;

        FrozenDictionary<ushort, SegmentExecutionCtx> SegmentCode((OpcodeInfo[], byte[][]) codeData)
        {
            Dictionary<ushort, SegmentExecutionCtx> opcodeInfos = [];

            List<OpcodeInfo> segment = [];
            foreach (var opcode in codeData.Item1)
            {
                if (opcode.Operation.IsStateful())
                {
                    if (segment.Count > 0)
                    {
                        opcodeInfos.Add(segment[0].ProgramCounter, ILCompiler.CompileSegment($"ILEVM_{Guid.NewGuid()}", segment.ToArray(), codeData.Item2));
                        segment.Clear();
                    }
                }
                else
                {
                    segment.Add(opcode);
                }
            }
            if (segment.Count > 0)
            {
                opcodeInfos.Add(segment[0].ProgramCounter, ILCompiler.CompileSegment($"ILEVM_{Guid.NewGuid()}", segment.ToArray(), codeData.Item2));
            }
            return opcodeInfos.ToFrozenDictionary();
        }

        FrozenDictionary<ushort, InstructionChunk> CheckPatterns(ReadOnlyMemory<byte> machineCode)
        {
            var (strippedBytecode, data) = StripByteCode(machineCode.Span);
            var patternFound = new Dictionary<ushort, InstructionChunk>();
            foreach (var (pattern, mapping) in Patterns)
            {
                for (int i = 0; i < strippedBytecode.Length - pattern.Length + 1; i++)
                {
                    bool found = true;
                    for (int j = 0; j < pattern.Length && found; j++)
                    {
                        found = ((byte)strippedBytecode[i + j].Operation == pattern[j]);
                    }

                    if (found)
                    {
                        patternFound.Add((ushort)i, mapping);
                        i += pattern.Length - 1;
                    }
                }
            }
            return patternFound.ToFrozenDictionary();
        }

        switch (mode)
        {
            case IlInfo.ILMode.PatternMatching:
                codeInfo.IlInfo.WithChunks(CheckPatterns(machineCode));
                break;
            case IlInfo.ILMode.SubsegmentsCompiling:
                codeInfo.IlInfo.WithSegments(SegmentCode(StripByteCode(machineCode.Span)));
                break;
        }
    }

    /// <summary>
    /// How many execution a <see cref="CodeInfo"/> should perform before trying to get its opcodes optimized.
    /// </summary>
    public static int CompoundOpThreshold = 32;
    public static int IlCompilerThreshold = 128;
}
