// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Diagnostics;
using Nethermind.Core.Crypto;

namespace Nethermind.Pbt;

/// <summary>Provides a validated, borrowed view of one canonical branch encoding.</summary>
/// <remarks>The caller must keep the backing memory alive and unchanged for the lifetime of the view.</remarks>
public readonly ref struct PbtBranchReader
{
    private readonly ReadOnlySpan<byte> _encoding;
    private readonly int _preimageLength;

    private PbtBranchReader(ReadOnlySpan<byte> encoding)
    {
        _encoding = encoding;
        _preimageLength = PbtNodeCodec.BranchPreimageLength(BinaryPrimitives.ReadUInt16BigEndian(encoding[1..]));
    }

    public static PbtBranchReader FromValidated(ReadOnlySpan<byte> encoding)
    {
        EnsureBranch(encoding);
        return new(encoding);
    }

    public CompressedPrefix Prefix => CompressedPrefix.FromValidated(_encoding[1..(_preimageLength - 64)]);
    /// <summary>The branch's EIP-8297 hash preimage, which starts its encoding.</summary>
    public ReadOnlySpan<byte> Preimage => _encoding[.._preimageLength];
    public ValueHash256 LeftHash => new(_encoding.Slice(_preimageLength - 64, 32));
    public ValueHash256 RightHash => new(_encoding.Slice(_preimageLength - 32, 32));
    /// <summary>The left child's key past <see cref="PbtNodeCodec.InlineKeyOffset"/> when it is a leaf, otherwise empty.</summary>
    public ReadOnlySpan<byte> LeftKeyPostfix => _encoding.Slice(_preimageLength + PbtNodeCodec.BranchTrailerHeaderLength, _encoding[_preimageLength]);
    /// <summary>The right child's key past <see cref="PbtNodeCodec.InlineKeyOffset"/> when it is a leaf, otherwise empty.</summary>
    public ReadOnlySpan<byte> RightKeyPostfix => _encoding[(_preimageLength + PbtNodeCodec.BranchTrailerHeaderLength + _encoding[_preimageLength])..];

    [Conditional("DEBUG")]
    private static void EnsureBranch(ReadOnlySpan<byte> encoding)
    {
        if (PbtNodeCodec.IsLeaf(encoding)) throw new InvalidOperationException("The PBT node is not a branch.");
    }
}
