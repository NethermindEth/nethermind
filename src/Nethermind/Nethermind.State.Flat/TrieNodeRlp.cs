// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nethermind.State.Flat;

/// <summary>
/// Fixed-size value type storing RLP-encoded trie node bytes inline, eliminating heap allocations.
/// </summary>
/// <remarks>
/// Branch node max RLP: 3 (sequence prefix) + 16 × 33 (prefix + 32-byte hash) + 1 (empty value slot) = 532 bytes.
/// Rounded up to 544 (next multiple of 32). Total struct size: 2 (Length) + 544 (data) = 546 bytes.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct TrieNodeRlp
{
    public const int MaxRlpLength = 544;

    /// <summary>Valid RLP byte count. 0 means empty/invalid.</summary>
    public ushort Length;

    private RlpBuffer _data;

    [InlineArray(MaxRlpLength)]
    private struct RlpBuffer { private byte _element; }

    /// <summary>Returns a read-only span over the valid RLP bytes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<byte> AsSpan() =>
        MemoryMarshal.CreateReadOnlySpan(
            ref Unsafe.As<RlpBuffer, byte>(ref _data),
            Math.Min((int)Length, MaxRlpLength));

    /// <summary>Returns a writable span over the full buffer capacity.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<byte> AsWritableSpan() =>
        MemoryMarshal.CreateSpan(ref Unsafe.As<RlpBuffer, byte>(ref _data), MaxRlpLength);

    /// <summary>
    /// Copies <paramref name="source"/> into the inline buffer and sets <see cref="Length"/>.
    /// Source must not exceed <see cref="MaxRlpLength"/> bytes.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Set(ReadOnlySpan<byte> source)
    {
        source.CopyTo(AsWritableSpan());
        Length = (ushort)source.Length;
    }

    /// <summary>Copies the valid RLP bytes into a new heap array.</summary>
    public byte[] ToArray() => AsSpan().ToArray();
}
