// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Serialization.Ssz.Merkleization;

/// <summary>Whether the guest's zkVM hashes merkle pairs with SP1's SHA-256 extend and compress precompiles.</summary>
/// <remarks>
/// Only SP1's runtime exports <c>zkvm_sha256_extend</c> and <c>zkvm_sha256_compress</c>, so the choice cannot be a
/// compile-time symbol here. <see cref="IsActive"/> is <see langword="false"/> as written; the SP1 guest's
/// <c>substitutions.xml</c> stubs it to <see langword="true"/> at link time. ILC then folds the check, so the SP1
/// guest calls the precompiles directly and the other guests never reference their symbols.
/// </remarks>
internal readonly struct Sp1Sha256Flag
{
    public static bool IsActive => false;
}
