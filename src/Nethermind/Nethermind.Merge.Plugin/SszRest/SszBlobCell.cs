// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nethermind.Merge.Plugin.SszRest;

/// <summary>
/// Inline 1024-byte Blob Cell representation used by Engine API SSZ wire types.
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 1024)]
public struct SszBlobCell
{
    public const int BlobCellLength = 1024;

    public static SszBlobCell FromSpan(ReadOnlySpan<byte> span)
    {
        if (span.Length != BlobCellLength)
        {
            throw new InvalidDataException($"{nameof(SszBlobCell)} expects input of length {BlobCellLength} and received {span.Length}");
        }

        SszBlobCell result = default;
        span.CopyTo(MemoryMarshal.CreateSpan(ref Unsafe.As<SszBlobCell, byte>(ref result), BlobCellLength));
        return result;
    }

    public ReadOnlySpan<byte> AsSpan() =>
        MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<SszBlobCell, byte>(ref Unsafe.AsRef(in this)), BlobCellLength);
}
