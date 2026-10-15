// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core.Crypto;

/// <summary>Whether the guest absorbs aligned Keccak input with OpenVM's XORIN instruction.</summary>
/// <remarks>
/// Only OpenVM's runtime exports <c>zkvm_keccak_xorin</c>, so the choice cannot be a compile-time symbol here.
/// <see cref="IsActive"/> is <see langword="false"/> as written; the OpenVM guest's <c>substitutions.xml</c> stubs it
/// to <see langword="true"/> at link time. ILC then folds the check, so the OpenVM guest calls the instruction
/// directly and the other guests never reference its symbol.
/// </remarks>
internal readonly struct OpenVmXorinFlag : IFlag
{
    /// <inheritdoc />
    public static bool IsActive => false;
}
