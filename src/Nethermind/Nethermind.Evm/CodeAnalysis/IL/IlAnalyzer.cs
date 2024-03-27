// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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
        lock(Patterns)
        {
            Patterns[pattern] = chunk;
        }
        return Patterns;
    }
    public static InstructionChunk GetPatternHandler(byte[] pattern)
    {
        return Patterns[pattern];
    }


    /// <summary>
    /// Starts the analyzing in a background task and outputs the value in the <paramref name="codeInfo"/>.
    /// </summary>
    /// <param name="codeInfo">The destination output.</param>
    public static Task StartAnalysis(CodeInfo codeInfo)
    {
        return Task.Run(() =>
        {
            IlInfo info = Analysis(codeInfo.MachineCode);
            codeInfo.SetIlInfo(info);
        });
    }

    /// <summary>
    /// For now, return null always to default to EVM.
    /// </summary>
    private static IlInfo Analysis(ReadOnlyMemory<byte> machineCode)
    {
        OpcodeInfo[] StripByteCode(ReadOnlySpan<byte> machineCode)
        {
            OpcodeInfo[] opcodes = new OpcodeInfo[machineCode.Length];
            int j = 0;
            for (ushort i = 0; i < machineCode.Length; i++, j++)
            {
                Instruction opcode = (Instruction)machineCode[i];
                byte[] args = null;
                ushort pc = i;
                if (opcode is > Instruction.PUSH0 and <= Instruction.PUSH32)
                {
                    ushort immediatesCount = opcode - Instruction.PUSH0;
                    args = machineCode.Slice(i + 1, immediatesCount).ToArray();
                    i += immediatesCount;
                }
                opcodes[j] = new OpcodeInfo(pc, opcode, args.AsMemory(), OpcodeMetadata.Operations.GetValueOrDefault(opcode));
            }
            return opcodes[..j];
        }

        FrozenDictionary<ushort, Func<long, EvmExceptionType>> SegmentCode(OpcodeInfo[] codeData)
        {
            Dictionary<ushort, Func<long, EvmExceptionType>> opcodeInfos = [];

            List<OpcodeInfo> segment = [];
            foreach (var opcode in codeData)
            {
                if (opcode.Operation.IsStateful())
                {
                    if (segment.Count > 0)
                    {
                        opcodeInfos.Add(segment[0].ProgramCounter, ILCompiler.CompileSegment($"ILEVM_{Guid.NewGuid()}", segment.ToArray()));
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
                opcodeInfos.Add(segment[0].ProgramCounter, ILCompiler.CompileSegment($"ILEVM_{Guid.NewGuid()}", segment.ToArray()));
            }
            return opcodeInfos.ToFrozenDictionary();
        }

        FrozenDictionary<ushort, InstructionChunk> CheckPatterns(ReadOnlyMemory<byte> machineCode, out OpcodeInfo[] strippedBytecode)
        {
            strippedBytecode = StripByteCode(machineCode.Span);
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

        return new IlInfo(CheckPatterns(machineCode, out OpcodeInfo[] strippedBytecode), SegmentCode(strippedBytecode));
    }

    /// <summary>
    /// How many execution a <see cref="CodeInfo"/> should perform before trying to get its opcodes optimized.
    /// </summary>
    public const int IlAnalyzerThreshold = 23;
    public const int IlCompilerThreshold = 57;
}
