// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Pbt;

namespace Nethermind.State.Pbt.Test;

/// <summary>The write formats the fixtures written around one tiling name it by.</summary>
internal static class PbtTestFormats
{
    /// <summary>The four-level clustered tiling, in <paramref name="groupFormat"/>.</summary>
    public static PbtTrieFormat Clustered(PbtGroupFormat groupFormat) => new(PbtTiling.ClusteredFourLevel, groupFormat);
}
