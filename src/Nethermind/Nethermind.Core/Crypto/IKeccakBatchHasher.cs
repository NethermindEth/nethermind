// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core.Crypto;

/// <summary>Hashes many independent inputs; implementations may vectorize or offload.</summary>
public interface IKeccakBatchHasher
{
    /// <summary>Computes keccak256 of each input in the batch, writing one hash per input.</summary>
    /// <param name="flat">Concatenated inputs, in order, with no separators.</param>
    /// <param name="offsets">
    /// Exclusive end offset of each input: <c>offsets[i]</c> is the end of input <c>i</c>; input <c>i</c> starts at
    /// <c>offsets[i-1]</c>, and the first input starts at 0.
    /// </param>
    /// <param name="outputs">Receives keccak256 of input <c>i</c> at index <c>i</c>.</param>
    /// <remarks>
    /// Contract every backend relies on; violating any of these is caller error (backends may throw or misbehave):
    /// <list type="bullet">
    /// <item><description><c>offsets.Length == outputs.Length</c>.</description></item>
    /// <item><description><c>offsets</c> is non-decreasing (a zero-length input is a repeated offset).</description></item>
    /// <item><description>When the batch is non-empty, <c>offsets[^1] == flat.Length</c> - no trailing bytes are silently ignored.</description></item>
    /// </list>
    /// </remarks>
    void HashBatch(ReadOnlySpan<byte> flat, ReadOnlySpan<int> offsets, Span<ValueHash256> outputs);
}
