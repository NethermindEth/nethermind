// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Globalization;
using System.Text.Json;
using Nethermind.Evm;

namespace Nethermind.Blockchain.Tracing.GethStyle;

/// <summary>Provides go-ethereum opcode names for trace output.</summary>
public static class OpcodeJsonNames
{
    private static readonly (string Name, JsonEncodedText JsonName)[] _lookup = BuildLookup();

    /// <summary>Gets the go-ethereum JSON name for an opcode.</summary>
    /// <param name="opcode">Opcode byte.</param>
    /// <returns>The pre-encoded opcode name.</returns>
    public static JsonEncodedText Get(Instruction opcode) => _lookup[(byte)opcode].JsonName;

    /// <summary>Gets the go-ethereum name for an opcode.</summary>
    /// <param name="opcode">Opcode byte.</param>
    /// <returns>The unescaped opcode name.</returns>
    public static string GetName(Instruction opcode) => _lookup[(byte)opcode].Name;

    private static (string Name, JsonEncodedText JsonName)[] BuildLookup()
    {
        (string Name, JsonEncodedText JsonName)[] table = new (string, JsonEncodedText)[256];
        for (int i = 0; i < 256; i++)
        {
            Instruction opcode = (Instruction)i;
            string name = (byte)opcode switch
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
            table[i] = (name, JsonEncodedText.Encode(name));
        }
        return table;
    }
}
