// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.State.Snap;

/// <summary>An entry of an <see cref="IPathWithAccountList"/>, its account still in slim RLP form.</summary>
public readonly ref struct RefPathWithAccount(ValueHash256 path, RefSlimAccount account)
{
    public ValueHash256 Path { get; } = path;
    public RefSlimAccount Account { get; } = account;
}
