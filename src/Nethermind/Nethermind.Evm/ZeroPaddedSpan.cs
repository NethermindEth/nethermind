// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices;

namespace Nethermind.Evm;

[StructLayout(LayoutKind.Auto)]
public readonly ref struct ZeroPaddedSpan
{
    public static ZeroPaddedSpan Empty => new(default, 0, PadDirection.Right);

    public ZeroPaddedSpan(ReadOnlySpan<byte> span, int paddingLength, PadDirection padDirection)
    {
        _reference = ref MemoryMarshal.GetReference(span);
        _length = span.Length;
        _paddingLength = padDirection == PadDirection.Right ? paddingLength : (paddingLength | int.MinValue);
    }

    private readonly ref byte _reference;  // 8 bytes (ByReference<byte>)
    private readonly int _length;          // 4 bytes
    private readonly int _paddingLength;   // 4 bytes

    public readonly PadDirection PadDirection => _paddingLength >= 0 ? PadDirection.Right : PadDirection.Left;
    public readonly ReadOnlySpan<byte> Span => MemoryMarshal.CreateReadOnlySpan(ref _reference, _length);
    public readonly int PaddingLength => _paddingLength & int.MaxValue;
    public int Length => Span.Length + PaddingLength;

    /// <summary>
    /// Temporary to handle old invocations
    /// </summary>
    /// <returns></returns>
    public byte[] ToArray()
    {
        byte[] result = new byte[Span.Length + PaddingLength];
        Span.CopyTo(result.AsSpan(PadDirection == PadDirection.Right ? 0 : PaddingLength, Span.Length));
        return result;
    }
}
