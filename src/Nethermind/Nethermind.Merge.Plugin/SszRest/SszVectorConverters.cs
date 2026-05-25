// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Ssz;

namespace Nethermind.Merge.Plugin.SszRest;

public sealed class Hash256SszVectorConverter : SszVectorConverter<Hash256>
{
    public const int Length = Hash256.Size;

    private Hash256SszVectorConverter() { }

    public static Hash256 FromSpan(ReadOnlySpan<byte> span) => new(span);

    public static void ToSpan(Span<byte> span, Hash256 value) => value.Bytes.CopyTo(span);
}

public sealed class AddressSszVectorConverter : SszVectorConverter<Address>
{
    public const int Length = Address.Size;

    private AddressSszVectorConverter() { }

    public static Address FromSpan(ReadOnlySpan<byte> span) => new(span);

    public static void ToSpan(Span<byte> span, Address value) => value.Bytes.CopyTo(span);
}

public sealed class BloomSszVectorConverter : SszVectorConverter<Bloom>
{
    public const int Length = Bloom.ByteLength;

    private BloomSszVectorConverter() { }

    public static Bloom FromSpan(ReadOnlySpan<byte> span) => new(span);

    public static void ToSpan(Span<byte> span, Bloom value) => value.Bytes.CopyTo(span);
}

/// <summary>
/// Inline 48-byte KZG commitment/proof representation used by Engine API SSZ wire types.
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 48)]
public struct SszKzgCommitment
{
    public const int KzgCommitmentLength = 48;

    public static SszKzgCommitment FromSpan(ReadOnlySpan<byte> span)
    {
        if (span.Length != KzgCommitmentLength)
        {
            throw new InvalidDataException($"{nameof(SszKzgCommitment)} expects input of length {KzgCommitmentLength} and received {span.Length}");
        }

        SszKzgCommitment result = default;
        span.CopyTo(MemoryMarshal.CreateSpan(ref Unsafe.As<SszKzgCommitment, byte>(ref result), KzgCommitmentLength));
        return result;
    }

    public ReadOnlySpan<byte> AsSpan() =>
        MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<SszKzgCommitment, byte>(ref Unsafe.AsRef(in this)), KzgCommitmentLength);
}

public sealed class SszKzgCommitmentVectorConverter : SszVectorConverter<SszKzgCommitment>
{
    public const int Length = SszKzgCommitment.KzgCommitmentLength;

    private SszKzgCommitmentVectorConverter() { }

    public static SszKzgCommitment FromSpan(ReadOnlySpan<byte> span) => SszKzgCommitment.FromSpan(span);

    public static void ToSpan(Span<byte> span, SszKzgCommitment value) => value.AsSpan().CopyTo(span);
}
