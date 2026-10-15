// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Core;

/// <summary>A recursive proof and its dependency digest, used by inclusion lists and proof retrieval.</summary>
public sealed class RecursiveStark(byte[] starkProof, Hash256 blockDepsHash)
{
    /// <summary>The serialized recursive STARK proof (opaque; produced by Lean Ethereum tooling).</summary>
    public byte[] StarkProof { get; } = starkProof;

    /// <summary>Commitment to all dependency triples in the block (spec <c>block_deps_hash</c>).</summary>
    public Hash256 BlockDepsHash { get; } = blockDepsHash;
}
