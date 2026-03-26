// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.State.Flat;

internal sealed class TrieNodeBranch
{
    public ushort Length;
    private BranchRlpBuffer _data;
    public ChildOffsetBuffer ChildOffsets;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<byte> AsSpan() =>
        MemoryMarshal.CreateReadOnlySpan(
            ref Unsafe.As<BranchRlpBuffer, byte>(ref _data),
            Math.Min((int)Length, BranchMaxRlpLength));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Set(ReadOnlySpan<byte> rlp)
    {
        rlp.CopyTo(MemoryMarshal.CreateSpan(ref Unsafe.As<BranchRlpBuffer, byte>(ref _data), BranchMaxRlpLength));
        Length = (ushort)rlp.Length;
    }

    public byte[] ToArray() => AsSpan().ToArray();

    /// <summary>
    /// Returns the 32-byte hash of child at <paramref name="index"/> by reading from the RLP at the stored offset.
    /// Returns <c>null</c> if the child offset is 0 (empty/absent).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Hash256? GetChildHash(int index)
    {
        short offset = ChildOffsets[index];
        if (offset == 0) return null;

        ReadOnlySpan<byte> rlp = AsSpan();
        // Child hash ref in RLP: 0xA0 prefix byte followed by 32 bytes of hash
        if (offset < rlp.Length && rlp[offset] == 0xA0 && offset + 33 <= rlp.Length)
        {
            return new Hash256(rlp.Slice(offset + 1, 32));
        }

        return null;
    }

    /// <summary>
    /// Returns the raw RLP bytes of the child item at <paramref name="index"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<byte> GetChildRlp(int index)
    {
        short offset = ChildOffsets[index];
        if (offset == 0) return ReadOnlySpan<byte>.Empty;

        ReadOnlySpan<byte> rlp = AsSpan();
        Rlp.ValueDecoderContext ctx = rlp[offset..].AsRlpValueContext();
        int itemLength = ctx.PeekNextRlpLength();
        return rlp.Slice(offset, itemLength);
    }

    public void ParseMetadata(ReadOnlySpan<byte> rlp)
    {
        Rlp.ValueDecoderContext ctx = rlp.AsRlpValueContext();
        int sequenceLength = ctx.ReadSequenceLength();
        int endPosition = ctx.Position + sequenceLength;

        for (int i = 0; i < 17 && ctx.Position < endPosition; i++)
        {
            byte prefix = rlp[ctx.Position];
            if (prefix == 0x80)
            {
                ChildOffsets[i] = 0;
                ctx.Position++;
            }
            else
            {
                ChildOffsets[i] = (short)ctx.Position;
                ctx.SkipItem();
            }
        }
    }

    public void Clear()
    {
        Length = 0;
        ChildOffsets = default;
    }

    public const int BranchMaxRlpLength = 544;

    [InlineArray(BranchMaxRlpLength)]
    private struct BranchRlpBuffer { private byte _element; }

    [InlineArray(17)]
    public struct ChildOffsetBuffer { private short _element; }
}

internal sealed class TrieNodeExtension
{
    public ushort Length;
    private ExtensionRlpBuffer _data;
    public short ChildOffset;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<byte> AsSpan() =>
        MemoryMarshal.CreateReadOnlySpan(
            ref Unsafe.As<ExtensionRlpBuffer, byte>(ref _data),
            Math.Min((int)Length, ExtensionMaxRlpLength));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Set(ReadOnlySpan<byte> rlp)
    {
        rlp.CopyTo(MemoryMarshal.CreateSpan(ref Unsafe.As<ExtensionRlpBuffer, byte>(ref _data), ExtensionMaxRlpLength));
        Length = (ushort)rlp.Length;
    }

    public byte[] ToArray() => AsSpan().ToArray();

    public void ParseMetadata(ReadOnlySpan<byte> rlp)
    {
        Rlp.ValueDecoderContext ctx = rlp.AsRlpValueContext();
        ctx.ReadSequenceLength();

        // Skip the compact-encoded key (first item)
        if (rlp[ctx.Position] < 0x80)
        {
            ctx.Position++;
        }
        else
        {
            (int prefixLen, int contentLen) = ctx.ReadPrefixAndContentLength();
            ctx.Position += contentLen;
        }

        // Store offset of the second item (child reference)
        ChildOffset = (short)ctx.Position;
    }

    public void Clear()
    {
        Length = 0;
        ChildOffset = 0;
    }

    public const int ExtensionMaxRlpLength = 96;

    [InlineArray(ExtensionMaxRlpLength)]
    private struct ExtensionRlpBuffer { private byte _element; }
}

internal sealed class TrieNodeLeaf
{
    public ushort Length;
    private LeafRlpBuffer _data;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<byte> AsSpan() =>
        MemoryMarshal.CreateReadOnlySpan(
            ref Unsafe.As<LeafRlpBuffer, byte>(ref _data),
            Math.Min((int)Length, LeafMaxRlpLength));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Set(ReadOnlySpan<byte> rlp)
    {
        rlp.CopyTo(MemoryMarshal.CreateSpan(ref Unsafe.As<LeafRlpBuffer, byte>(ref _data), LeafMaxRlpLength));
        Length = (ushort)rlp.Length;
    }

    public byte[] ToArray() => AsSpan().ToArray();

    public void Clear() => Length = 0;

    public const int LeafMaxRlpLength = 160;

    [InlineArray(LeafMaxRlpLength)]
    private struct LeafRlpBuffer { private byte _element; }
}
