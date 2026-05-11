// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nethermind.Serialization.Ssz;

/// <summary>
/// 48-byte fixed-size byte vector used for KZG commitments and proofs in
/// engine-API blob bundles. Stored inline so an <c>SszKzgCommitment[]</c>
/// is a single contiguous heap allocation of <c>count * 48</c> bytes — no
/// per-element <c>byte[48]</c> headers. SSZ wire format for a fixed-size
/// byte vector is just the raw bytes, so the basic-type custom encode/decode
/// templates produce identical output to the previous
/// <c>[SszContainer]</c>-with-<c>byte[]</c> shape.
/// </summary>
[InlineArray(KzgCommitmentLength)]
public struct SszKzgCommitment
{
    public const int KzgCommitmentLength = 48;
    private byte _e0;

    public static SszKzgCommitment FromSpan(ReadOnlySpan<byte> span)
    {
        if (span.Length != KzgCommitmentLength)
            throw new ArgumentException($"SszKzgCommitment requires exactly {KzgCommitmentLength} bytes", nameof(span));

        SszKzgCommitment result = default;
        span.CopyTo(MemoryMarshal.CreateSpan(ref Unsafe.As<SszKzgCommitment, byte>(ref result), KzgCommitmentLength));
        return result;
    }

    public ReadOnlySpan<byte> AsSpan() =>
        MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<SszKzgCommitment, byte>(ref Unsafe.AsRef(in this)), KzgCommitmentLength);
}
