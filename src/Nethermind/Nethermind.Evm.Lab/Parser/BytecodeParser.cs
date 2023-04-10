// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Evm.Lab.Parser;

public record Immediate(int size, byte[] value);
public record Instruction(int idx, Evm.Instruction opcode, Immediate? arg = null)
{
    public override string ToString()
        => $"{opcode} {(arg is null ? string.Empty : arg?.value?.ToHexString(true))}";
}
internal class BytecodeParser
{
    public static IReadOnlyList<Instruction> Parse(string code)
    {
        var tokens = Regex.Replace(code, @"\s+", " ").Split(' ');
        var parsed = new List<Instruction>();

        int i = 0; int idx = 0;
        while(i < tokens.Length)
        {
            if (Enum.TryParse<Evm.Instruction>(tokens[i++], out Evm.Instruction opcode))
            {
                idx++;
                int immCount = opcode.StackRequirements().immediates;
                if (immCount > 0)
                {
                    parsed.Add(new Instruction(idx, opcode, new Immediate(immCount, Bytes.FromHexString(tokens[i++]))));
                    idx += immCount;
                } else
                {
                    parsed.Add(new Instruction(idx, opcode));
                }
            } else throw new InvalidCodeException();
        }
        return parsed;
    }

    public static IReadOnlyList<Instruction> Dissassemble(byte[] bytecode)
    {
        var opcodes = new List<Instruction>();

        for (int i = 0; i < bytecode.Length; i++)
        {
            var instruction = (Evm.Instruction)bytecode[i];
            if (!instruction.IsValid())
            {
                throw new InvalidOperationException();
            }
            int immediatesCount = instruction.GetImmediateCount();
            byte[] immediates = bytecode.Slice(i + 1, immediatesCount);
            opcodes.Add(new Instruction(i, instruction, new Immediate(immediatesCount, immediatesCount == 0 ? null : immediates)));
            i += immediatesCount;
        }
        return opcodes;
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

    public static string ToMultiLineString(this IReadOnlyList<Instruction> opcodes)
    {
        return String.Join("\n", opcodes.Select(instr => instr.ToString()));
    }
}
