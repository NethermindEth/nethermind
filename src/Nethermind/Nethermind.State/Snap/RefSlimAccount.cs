// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Serialization.Rlp;

namespace Nethermind.State.Snap;

/// <summary>An account in the SNAP slim RLP format, decoded on demand from the span it wraps.</summary>
public readonly ref struct RefSlimAccount(ReadOnlySpan<byte> slimRlp)
{
    public ReadOnlySpan<byte> Rlp { get; } = slimRlp;

    /// <exception cref="RlpException">The span holds the placeholder encoding that carries no account.</exception>
    public AccountStruct ToStruct() =>
        AccountDecoder.Slim.TryDecodeStruct(Rlp, out AccountStruct account)
            ? account
            : throw new RlpException("A SNAP account entry must contain an account value.");

    /// <inheritdoc cref="ToStruct"/>
    public Account ToAccount() => ToStruct().ToAccount();
}
