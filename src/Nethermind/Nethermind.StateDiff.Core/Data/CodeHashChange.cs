// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.StateDiff.Core.Data;

/// <summary>Per-account code-hash transition across one diff.</summary>
public readonly record struct CodeHashChange(
    ValueHash256 AddressHash,
    ValueHash256 OldCodeHash,
    ValueHash256 NewCodeHash)
{
    /// <summary>All-zero sentinel for "no code"; distinct from the empty-bytecode hash.</summary>
    public static ValueHash256 NoCode => default;

    public bool HadCode => OldCodeHash != NoCode;
    public bool HasCode => NewCodeHash != NoCode;
}
