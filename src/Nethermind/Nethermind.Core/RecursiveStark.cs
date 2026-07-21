// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Core;

/// <summary>
/// EIP-8288 block-header entry <c>recursive_stark = [stark_proof, block_deps_hash]</c>: the recursive
/// STARK proving the validity of every dependency declared by the block's transactions, together with
/// the commitment to those dependencies.
/// https://eips.ethereum.org/EIPS/eip-8288
/// </summary>
public sealed class RecursiveStark(byte[] starkProof, Hash256 blockDepsHash)
{
    /// <summary>The serialized recursive STARK proof (opaque; produced by Lean Ethereum tooling).</summary>
    public byte[] StarkProof { get; } = starkProof;

    /// <summary>Commitment to all dependency triples in the block (spec <c>block_deps_hash</c>).</summary>
    public Hash256 BlockDepsHash { get; } = blockDepsHash;
}
