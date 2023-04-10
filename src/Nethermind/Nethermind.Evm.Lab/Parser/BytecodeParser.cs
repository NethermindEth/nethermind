// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Evm.Lab.Parser;

public record Immediate(int size, UInt256 value);
public record Instruction(Evm.Instruction opcode, Immediate? arg = null);
internal class BytecodeParser
{
    public static List<Instruction> Parse(string code)
    {
        var tokens = Regex.Replace(code, @"\s+", " ").Split(' ');
        var parsed = new List<Instruction>();

        int i = 0;
        while(i < tokens.Length)
        {
            if (Enum.TryParse<Evm.Instruction>(tokens[i++], out Evm.Instruction opcode))
            {
                int immCount = opcode.StackRequirements().immediates;
                if (immCount > 0)
                {
                    parsed.Add(new Instruction(opcode, new Immediate(immCount, Int256.UInt256.Parse(tokens[i++]))));
                } else
                {
                    parsed.Add(new Instruction(opcode));
                }
            } else throw new InvalidCodeException();
        }
        return parsed;
    }
}

public static class Extension
{
    public static byte[] ToByteArray(this List<Instruction> opcodes)
    {
        return opcodes.SelectMany(instr=>
        {
            byte[] allocatedArr = new byte[1 + (instr?.arg?.size ?? 0)];
            allocatedArr[0] = (byte)instr.opcode;
            if(instr.arg is not null) {
                Array.Copy(instr.arg.value.ToBigEndian()[^instr.arg.size..], 0, allocatedArr, 1, instr.arg.size);
            }
            return allocatedArr;
        }).ToArray();
    }
}
