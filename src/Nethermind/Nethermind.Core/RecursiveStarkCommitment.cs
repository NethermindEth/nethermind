// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Core;

/// <summary>The EIP-8288 header commitment to the body proof and its dependency statement.</summary>
public sealed class RecursiveStarkCommitment(Hash256 starkProofHash, Hash256 blockDepsHash)
{
    /// <summary>Keccak-256 of the exact proof bytes carried in the block body.</summary>
    public Hash256 StarkProofHash { get; } = starkProofHash;

    /// <summary>The digest of the block's canonical dependency union.</summary>
    public Hash256 BlockDepsHash { get; } = blockDepsHash;

    /// <summary>Whether both commitments are present and an empty statement commits to an empty proof.</summary>
    public bool HasValidShape => StarkProofHash is not null && BlockDepsHash is not null
        && (BlockDepsHash.ValueHash256 != Eip8288Dependencies.ComputeDepsHash([]) || StarkProofHash == Keccak.OfAnEmptyString);
}
