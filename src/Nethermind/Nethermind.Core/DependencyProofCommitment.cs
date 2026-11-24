// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Core;

/// <summary>The EIP-8288 header fields <c>dependency_proof_hash</c> and <c>block_deps_hash</c>.</summary>
public sealed class DependencyProofCommitment(Hash256 dependencyProofHash, Hash256 blockDepsHash)
{
    /// <summary>The commitment of a block without dependencies: an empty proof over an empty statement, as at genesis.</summary>
    public static DependencyProofCommitment Empty { get; } = new(Keccak.OfAnEmptyString, new Hash256(Eip8288Dependencies.ComputeDepsHash([])));

    /// <summary>Keccak-256 of the exact <c>dependency_proof</c> bytes; that of the empty string for an empty proof.</summary>
    public Hash256 DependencyProofHash { get; } = dependencyProofHash;

    /// <summary>The digest of the block's canonical dependency union.</summary>
    public Hash256 BlockDepsHash { get; } = blockDepsHash;

    /// <summary>Whether both commitments are present and an empty statement commits to an empty proof.</summary>
    public bool HasValidShape => DependencyProofHash is not null && BlockDepsHash is not null
        && (BlockDepsHash.ValueHash256 != Eip8288Dependencies.ComputeDepsHash([]) || DependencyProofHash == Keccak.OfAnEmptyString);
}
