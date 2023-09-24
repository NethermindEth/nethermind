// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Evm.Lab.Parser;

public record Immediate(int size, byte[] value);
public record Instruction(int idx, Evm.Instruction opcode, Immediate? arg = null)
{
    public string ToString(IReleaseSpec spec)
        => $"{opcode.GetName(spec.IncludePush0Instruction, spec)} {(arg is null ? string.Empty : arg?.value?.ToHexString(true))}";
}
internal class BytecodeParser
{
    public static IReadOnlyList<Instruction> Parse(string code)
    {
        var tokens = Regex.Replace(code, @"\s+", " ").Split(' ');
        var parsed = new List<Instruction>();

        int i = 0; int idx = 0;
        while (i < tokens.Length)
        {
            if (Enum.TryParse<Evm.Instruction>(tokens[i++], out Evm.Instruction opcode))
            {
                idx++;
                int immCount = GetImmediateCount(opcode);
                if (immCount > 0)
                {
                    parsed.Add(new Instruction(idx, opcode, new Immediate(immCount, Bytes.FromHexString(tokens[i++]))));
                    idx += immCount;
                }
                else
                {
                    parsed.Add(new Instruction(idx, opcode));
                }
            }
            else
            {
                throw new InvalidCodeException();
            }
        }
        return parsed;
    }

    public static IReadOnlyList<Instruction> Dissassemble(ReadOnlySpan<byte> bytecode, bool isTraceSourced = false, int offsetInstructionIndexesBy = 0)
    {
        var opcodes = new List<Instruction>();
        for (int i = 0, j = 0; i < bytecode.Length; i++, j++)
        {
            var instruction = (Evm.Instruction)bytecode[i];
            if (!IsValid(instruction))
            {
                opcodes.Add(new Instruction(i, Evm.Instruction.INVALID));
                continue;
            }
            int immediatesCount = GetImmediateCount(instruction);
            ReadOnlySpan<byte> immediates = bytecode.Slice(i + 1, immediatesCount);
            opcodes.Add(new Instruction(offsetInstructionIndexesBy + (isTraceSourced ? j : i), instruction, new Immediate(immediatesCount, immediatesCount == 0 ? null : immediates.ToArray())));
            i += immediatesCount;
        }
        return opcodes;
    }

    private static bool IsValid(Evm.Instruction instruction)
    {
        return Enum.IsDefined<Evm.Instruction>(instruction);
    }

    private static int GetImmediateCount(Evm.Instruction instruction)
    {
        if(instruction is >= Evm.Instruction.PUSH0 and <= Evm.Instruction.PUSH32)
        {
            return instruction - Evm.Instruction.PUSH0;
        }
        return 0;
    }

    public static byte[] ExtractBytecodeFromTrace(GethLikeTxTrace trace)
    {
        //only works for non-eof code (immediate arguments are non-deducable
        (bool isPreviousOpcodePush, int byteCount) = (false, 0);
        var bytecode = new List<byte>();
        for (int i = 0; i < trace.Entries.Count; i++)
        {
            if (isPreviousOpcodePush)
            {
                byte[] stakcItem = Bytes.FromHexString(trace.Entries[i].Stack.Last());
                foreach (var byteElement in stakcItem.Slice(stakcItem.Length - byteCount))
                {
                    bytecode.Add(byteElement);
                }
            }
            Enum.TryParse<Evm.Instruction>(trace.Entries[i].Opcode, out Evm.Instruction opcode);
            bytecode.Add((byte)opcode);
            isPreviousOpcodePush = opcode is >= Evm.Instruction.PUSH1 && opcode <= Evm.Instruction.PUSH32;
            byteCount = opcode - Evm.Instruction.PUSH0;
        }
        return bytecode.ToArray();
    }
}

public static class Extension
{
    public static byte[] ToByteArray(this IReadOnlyList<Instruction> opcodes)
    {
        return opcodes.SelectMany(instr =>
        {
            byte[] allocatedArr = new byte[1 + (instr?.arg?.size ?? 0)];
            allocatedArr[0] = (byte)instr.opcode;
            if (instr.arg is not null)
            {
                Array.Copy(instr.arg.value, 0, allocatedArr, 1, instr.arg.size);
            }
            return allocatedArr;
        }).ToArray();
    }

    public static string ToMultiLineString(this IReadOnlyList<Instruction> opcodes, IReleaseSpec spec)
    {
        return String.Join("\n", opcodes.Select(instr => instr.ToString(spec)));
    }
}
