// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.FluentRlp;
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
        RlpReader pair = new RlpReader(rlp).EnterSequence(out _);
        _pathOffset = rlp.Length - pair.Remaining;
        pair.SkipItem();
        _accountOffset = rlp.Length - pair.Remaining;
    }

    public ValueHash256 Path
    {
        get
        {
            RlpReader reader = new(_rlp[_pathOffset.._accountOffset]);
            return reader.DecodeValueKeccakNonNull();
        }
    }

    public RefSlimAccount Account => new(_rlp[_accountOffset..]);
}
