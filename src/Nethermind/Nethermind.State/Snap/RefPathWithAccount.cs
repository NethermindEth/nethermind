// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.State.Snap;

/// <summary>An entry of an <see cref="IPathWithAccountList"/>, read from its <c>[path, slimAccount]</c> RLP.</summary>
public readonly ref struct RefPathWithAccount
{
    public RefPathWithAccount(ReadOnlySpan<byte> rlp)
    {
        RlpReader reader = new(rlp);
        reader.ReadSequenceLength();
        Path = reader.DecodeValueKeccakNonNull();
        Account = new RefSlimAccount(rlp[reader.Position..]);
    }

    public ValueHash256 Path { get; }
    public RefSlimAccount Account { get; }
}
