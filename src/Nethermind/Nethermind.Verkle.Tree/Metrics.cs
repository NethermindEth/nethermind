// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;

namespace Nethermind.Verkle.Tree;

public static class Metrics
{
    [Description("Number of trie node hash calculations.")]
    public static long TreeNodeHashCalculations { get; set; }

    [Description("Number of trie node RLP encodings.")]
    public static long TreeNodeRlpEncodings { get; set; }

    [Description("Number of trie node RLP decodings.")]
    public static long TreeNodeRlpDecodings { get; set; }
}
