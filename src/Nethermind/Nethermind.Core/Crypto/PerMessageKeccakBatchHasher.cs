// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices;

namespace Nethermind.Core.Crypto;

/// <summary>Per-message batch hasher: hashes each input independently via <see cref="KeccakHash.ComputeHash"/>.</summary>
/// <remarks>
/// Deliberately not named "scalar": on AVX-512 hardware <see cref="KeccakHash.ComputeHash"/> already runs a horizontal
/// vectorized permutation, so this is the per-message baseline all other backends are measured against, not a scalar path.
/// </remarks>
public sealed class PerMessageKeccakBatchHasher : IKeccakBatchHasher
{
    /// <inheritdoc/>
    public void HashBatch(ReadOnlySpan<byte> flat, ReadOnlySpan<int> offsets, Span<ValueHash256> outputs)
    {
        if (offsets.Length != outputs.Length) ThrowLengthMismatch();
        // O(1) trailing-bytes guard; non-monotonic offsets fail naturally via the range slice below.
        if (offsets.Length > 0 && offsets[^1] != flat.Length) ThrowLastOffsetMismatch();

        int start = 0;
        for (int i = 0; i < offsets.Length; i++)
        {
            int end = offsets[i];
            ReadOnlySpan<byte> input = flat[start..end];
            Span<byte> output = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref outputs[i], 1));
            KeccakHash.ComputeHash(input, output);
            start = end;
        }
    }

    private static void ThrowLengthMismatch() =>
        throw new ArgumentException("offsets and outputs must have equal length.");

    private static void ThrowLastOffsetMismatch() =>
        throw new ArgumentException("Last offset must equal the flat input length.");
}
