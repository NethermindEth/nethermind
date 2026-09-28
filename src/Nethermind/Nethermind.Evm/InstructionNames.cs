// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Globalization;

namespace Nethermind.Evm;

/// <summary>Provides go-ethereum names for executed and unsupported opcode bytes.</summary>
public static class InstructionNames
{
    /// <summary>Returns the opcode name, including Geth's names for unsupported EOF instructions.</summary>
    public static string GetName(Instruction opcode) => (byte)opcode switch
    {
        0x44 => "DIFFICULTY",
        0xd0 => "DATALOAD",
        0xd1 => "DATALOADN",
        0xd2 => "DATASIZE",
        0xd3 => "DATACOPY",
        0xe0 => "RJUMP",
        0xe1 => "RJUMPI",
        0xe2 => "RJUMPV",
        0xe3 => "CALLF",
        0xe4 => "RETF",
        0xe5 => "JUMPF",
        0xec => "EOFCREATE",
        0xee => "RETURNCONTRACT",
        0xf7 => "RETURNDATALOAD",
        0xf8 => "EXTCALL",
        0xf9 => "EXTDELEGATECALL",
        0xfb => "EXTSTATICCALL",
        byte value => Enum.GetName(opcode) ?? string.Create(CultureInfo.InvariantCulture, $"opcode 0x{value:x} not defined"),
    };
}
