// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Diagnostics;
using Nethermind.Core.Crypto;

namespace Nethermind.Pbt;

/// <summary>Provides a validated, borrowed view of one canonical node encoding.</summary>
/// <remarks>The caller must keep the backing memory alive and unchanged for the lifetime of the view.</remarks>
internal readonly ref struct PbtNodeReader
{
    private readonly ReadOnlySpan<byte> _encoding;

    private PbtNodeReader(ReadOnlySpan<byte> encoding) => _encoding = encoding;

    internal static PbtNodeReader FromValidated(ReadOnlySpan<byte> encoding) => new(encoding);

    internal ReadOnlySpan<byte> Encoding => _encoding;
    internal bool IsLeaf => _encoding[0] == 0;
    /// <summary>The root leaf's complete key.</summary>
    internal ReadOnlySpan<byte> Key => _encoding[2..];
    internal CompressedPrefix Prefix
    {
        get
        {
            EnsureBranch();
            return CompressedPrefix.FromValidated(_encoding.Slice(1, 2 + PbtBitPrefix.ByteCount(BitCount)));
        }
    }
    /// <summary>The branch's EIP-8297 hash preimage, which starts its encoding.</summary>
    internal ReadOnlySpan<byte> Preimage { get { EnsureBranch(); return _encoding[..PreimageLength]; } }
    internal ValueHash256 LeftHash { get { EnsureBranch(); return new(_encoding.Slice(PreimageLength - 64, 32)); } }
    internal ValueHash256 RightHash { get { EnsureBranch(); return new(_encoding.Slice(PreimageLength - 32, 32)); } }
    /// <summary>The left child's key past <see cref="PbtNodeCodec.InlineKeyOffset"/> when it is a leaf, otherwise empty.</summary>
    internal ReadOnlySpan<byte> LeftKeyPostfix { get { EnsureBranch(); return _encoding.Slice(PreimageLength + PbtNodeCodec.BranchTrailerHeaderLength, _encoding[PreimageLength]); } }
    /// <summary>The right child's key past <see cref="PbtNodeCodec.InlineKeyOffset"/> when it is a leaf, otherwise empty.</summary>
    internal ReadOnlySpan<byte> RightKeyPostfix { get { EnsureBranch(); return _encoding[(PreimageLength + PbtNodeCodec.BranchTrailerHeaderLength + _encoding[PreimageLength])..]; } }

    private int BitCount => BinaryPrimitives.ReadUInt16BigEndian(_encoding[1..]);
    private int PreimageLength => PbtNodeCodec.BranchPreimageLength(BitCount);

    [Conditional("DEBUG")]
    private void EnsureBranch()
    {
        if (IsLeaf) throw new InvalidOperationException("The PBT node is not a branch.");
    }
}
