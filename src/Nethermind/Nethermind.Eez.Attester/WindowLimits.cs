// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Eez.Attester;

/// <summary>How much one <c>Prove</c> request may carry.</summary>
/// <param name="MaxBytes">Canonical protobuf size of all chunks, header included.</param>
/// <param name="MaxWitnessItems">Trie nodes, codes, keys and headers across all block witnesses.</param>
public sealed record WindowLimits(int MaxBlocks, long MaxBytes, long MaxWitnessItems)
{
    public static WindowLimits Default { get; } = new(512, 536_870_912, 1_000_000);

    /// <summary>The largest single message decoded, the byte quota capped at 256 MiB.</summary>
    public int MaxMessageBytes => (int)System.Math.Min(MaxBytes, 256 * 1024 * 1024);
}
