// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Serialization.Ssz.Merkleization;

/// <summary>Whether the guest's zkVM hashes merkle pairs with OpenVM's SHA-256 compression instruction.</summary>
/// <remarks>
/// Only OpenVM's runtime implements <c>zkvm_sha256_compress</c> with this three-operand signature, so the choice cannot be
/// a compile-time symbol here. <see cref="IsActive"/> is <see langword="false"/> as written; the OpenVM guest's
/// <c>substitutions.xml</c> stubs it to <see langword="true"/> at link time. ILC then folds the check, so the OpenVM
/// guest calls the instruction directly and the other guests never reference its symbol. SP1 exports the same symbol
/// with two operands behind <see cref="Sp1Sha256Flag"/>; each guest switches on only its own flag.
/// </remarks>
internal readonly struct OpenVmSha256Flag
{
    public static bool IsActive => false;
}
