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
    public static Task StartAnalysis(ReadOnlyMemory<byte> machineCode, CodeInfo codeInfo)
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
        byte[] StripByteCode(ReadOnlySpan<byte> machineCode)
        {
            byte[] opcodes = new byte[machineCode.Length];
            int j = 0;
            for (int i = 0; i < machineCode.Length; i++, j++)
            {
                Instruction opcode = (Instruction)machineCode[i];
                opcodes[j] = (byte)opcode;
                if (opcode is > Instruction.PUSH0 and <= Instruction.PUSH32)
                {
                    int immediatesCount = opcode - Instruction.PUSH0;
                    i += immediatesCount;
                }
            }
            return opcodes[..j];
        }

        byte[] strippedBytecode = StripByteCode(machineCode.Span);
        Dictionary<ushort, InstructionChunk> patternFound = new Dictionary<ushort, InstructionChunk>();

        foreach (var (pattern, mapping) in Patterns)
        {
            for (int i = 0; i < strippedBytecode.Length - pattern.Length + 1; i++)
            {
                bool found = true;
                for (int j = 0; j < pattern.Length && found; j++)
                {
                    found = strippedBytecode[i + j] == pattern[j];
                }

                if (found)
                {
                    patternFound.Add((ushort)i, mapping);
                    i += pattern.Length - 1;
                }
            }
        }

        // TODO: implement actual analysis.
        return new IlInfo(patternFound.ToFrozenDictionary());
    }

    /// <summary>
    /// How many execution a <see cref="CodeInfo"/> should perform before trying to get its opcodes optimized.
    /// </summary>
    public const int IlAnalyzerThreshold = 23;
}
