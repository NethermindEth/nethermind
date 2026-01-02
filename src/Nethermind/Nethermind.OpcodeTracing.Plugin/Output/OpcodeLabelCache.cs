// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FastEnumUtility;
using Nethermind.Evm;

namespace Nethermind.OpcodeTracing.Plugin.Output;

/// <summary>
/// Provides a pre-built cache for mapping opcode bytes to human-readable labels.
/// </summary>
internal static class OpcodeLabelCache
{
    private static readonly string[] Labels = BuildLabels();

    /// <summary>
    /// Gets the human-readable label for the specified opcode byte.
    /// </summary>
    /// <param name="opcode">The opcode byte value.</param>
    /// <returns>The opcode name if known, otherwise a hex string like "0xfe".</returns>
    public static string GetLabel(byte opcode) => Labels[opcode];

    private static string[] BuildLabels()
    {
        string[] labels = new string[256];
        for (int i = 0; i < labels.Length; i++)
        {
            Instruction opcode = (Instruction)i;
            labels[i] = FastEnum.IsDefined(opcode)
                ? FastEnum.GetName(opcode)!
                : $"0x{i:x2}";
        }

        return labels;
    }
}
