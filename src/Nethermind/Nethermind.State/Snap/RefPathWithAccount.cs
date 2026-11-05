// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.State.Snap;

/// <summary>An entry of an <see cref="IPathWithAccountList"/>, read from its <c>[path, slimAccount]</c> RLP.</summary>
public readonly ref struct RefPathWithAccount
{
    private readonly ReadOnlySpan<byte> _rlp;
    private readonly int _pathOffset;
    private readonly int _accountOffset;

    public RefPathWithAccount(ReadOnlySpan<byte> rlp)
    {
        _rlp = rlp;
        RlpReader reader = new(rlp);
        reader.ReadSequenceLength();
        _pathOffset = reader.Position;
        reader.SkipItem();
        _accountOffset = reader.Position;
    }

    public ValueHash256 Path => new RlpReader(_rlp[_pathOffset.._accountOffset]).DecodeValueKeccakNonNull();

    public RefSlimAccount Account => new(_rlp[_accountOffset..]);
}
