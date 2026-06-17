// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nethermind.Merge.Plugin.SszRest;

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
