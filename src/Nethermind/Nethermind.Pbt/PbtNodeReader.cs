// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core.Crypto;

namespace Nethermind.Pbt;

/// <summary>Provides a validated, borrowed view of one canonical node encoding.</summary>
/// <remarks>The caller must keep the backing memory alive and unchanged for the lifetime of the view.</remarks>
internal readonly ref struct PbtNodeReader
{
    private readonly ReadOnlySpan<byte> _encoding;

    internal PbtNodeReader(ReadOnlySpan<byte> encoding)
    {
        PbtNodeCodec.ValidateExact(encoding);
        _encoding = encoding;
    }

    internal ReadOnlySpan<byte> Encoding { get { EnsureInitialized(); return _encoding; } }
    internal bool IsLeaf => Encoding[0] == 0;
    internal ReadOnlySpan<byte> Key { get { EnsureKind(true); return _encoding.Slice(3, Length); } }
    internal ReadOnlySpan<byte> Value { get { EnsureKind(true); return _encoding[^32..]; } }
    internal CompressedPrefix Prefix
    {
        get
        {
            EnsureKind(false);
            return CompressedPrefix.FromValidated(_encoding.Slice(1, 2 + PbtBitPrefix.ByteCount(Length)));
        }
    }
    internal ValueHash256 LeftHash { get { EnsureKind(false); return new(_encoding.Slice(_encoding.Length - 64, 32)); } }
    internal ValueHash256 RightHash { get { EnsureKind(false); return new(_encoding[^32..]); } }

    private int Length => BinaryPrimitives.ReadUInt16BigEndian(_encoding[1..]);

    private void EnsureInitialized()
    {
        if (_encoding.IsEmpty) throw new InvalidOperationException("The PBT node reader is uninitialized.");
    }

    private void EnsureKind(bool leaf)
    {
        if (IsLeaf != leaf) throw new InvalidOperationException("The PBT node has a different kind.");
    }
}
