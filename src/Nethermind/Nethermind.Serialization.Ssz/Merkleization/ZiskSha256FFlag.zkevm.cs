// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Serialization.Ssz.Merkleization;

/// <summary>Whether the guest's zkVM hashes merkle pairs with ZisK's SHA-256 compression precompile.</summary>
/// <remarks>
/// The three guests link one managed closure, and only ZisK's runtime exports <c>syscall_sha256_f</c>, so the
/// choice cannot be a compile-time symbol here. <see cref="IsActive"/> is <see langword="false"/> as written; the
/// ZisK guest's <c>substitutions.xml</c> stubs it to <see langword="true"/> at link time. ILC then folds the check,
/// so the ZisK guest calls the precompile directly and the other guests never reference its symbol.
/// </remarks>
internal readonly struct ZiskSha256FFlag
{
    public static bool IsActive => false;
}
