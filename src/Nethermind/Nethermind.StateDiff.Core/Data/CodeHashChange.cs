// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.StateDiff.Core.Data;

/// <summary>
/// Per-account code-hash transition across one diff. The <see cref="ValueHash256"/>
/// <see cref="NoCode"/> sentinel (all-zero) represents "no code" — either the
/// account never had code (gained it in this diff) or lost it entirely. Distinct
/// from <see cref="Keccak.OfAnEmptyString"/>, which is the empty-bytecode hash.
/// </summary>
public readonly record struct CodeHashChange(
    ValueHash256 AddressHash,
    ValueHash256 OldCodeHash,
    ValueHash256 NewCodeHash)
{
    /// <summary>
    /// Sentinel <see cref="ValueHash256"/> used when an account has no code on one
    /// side of the diff. Using <c>default</c> (all zeros) keeps every "gained code" /
    /// "lost code" event unambiguous, even when an account temporarily holds the
    /// empty-string code hash.
    /// </summary>
    public static ValueHash256 NoCode => default;

    public bool HadCode => OldCodeHash != NoCode;
    public bool HasCode => NewCodeHash != NoCode;
}
