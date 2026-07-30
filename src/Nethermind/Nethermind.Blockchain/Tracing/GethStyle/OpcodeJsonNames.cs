// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using Nethermind.Evm;

namespace Nethermind.Blockchain.Tracing.GethStyle;

internal static class OpcodeJsonNames
{
    private static readonly JsonEncodedText[] _names = BuildLookup();

    public static JsonEncodedText Get(Instruction opcode) => _names[(byte)opcode];

    private static JsonEncodedText[] BuildLookup()
    {
        JsonEncodedText[] table = new JsonEncodedText[256];
        for (int i = 0; i < 256; i++)
        {
            string name = Enum.GetName((Instruction)i) ?? ((byte)i).ToString("X2");
            table[i] = JsonEncodedText.Encode(name);
        }
        return table;
    }
}
