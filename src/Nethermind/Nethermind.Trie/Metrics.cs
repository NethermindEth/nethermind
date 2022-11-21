// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;

namespace Nethermind.Trie
{
    public static class Metrics
    {
        [Description("Number of trie node hash calculations.")]
        public static long TreeNodeHashCalculations { get; set; }

        [Description("Number of trie node RLP encodings.")]
        public static long TreeNodeRlpEncodings { get; set; }

        [Description("Number of trie node RLP decodings.")]
        public static long TreeNodeRlpDecodings { get; set; }
    }
}
