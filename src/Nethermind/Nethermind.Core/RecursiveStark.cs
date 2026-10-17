// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Core;

/// <summary>A recursive proof and the dependency digest it verifies against, as carried by mode-1 mempool wrappers.</summary>
public sealed class RecursiveStark(byte[] starkProof, Hash256 blockDepsHash)
{
    /// <summary>The serialized recursive STARK proof (opaque; produced by Lean Ethereum tooling).</summary>
    public byte[] StarkProof { get; } = starkProof;

    /// <summary>The proof's public input, <c>get_deps_hash</c> of the dependencies it covers.</summary>
    public Hash256 BlockDepsHash { get; } = blockDepsHash;
}
