// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
            string name = InstructionNames.GetName(opcode);
            table[i] = (name, JsonEncodedText.Encode(name));
        }
        return table;
    }
}
