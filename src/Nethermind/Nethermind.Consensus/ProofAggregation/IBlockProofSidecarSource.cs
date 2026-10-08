// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;

namespace Nethermind.Consensus.ProofAggregation;

/// <summary>Recovers the EIP-8288 <c>recursive_stark</c> a block header commits to when the block arrived without it.</summary>
/// <remarks>
/// The returned entry is only bound to the block hash. Block validation still checks <c>block_deps_hash</c> against the
/// body's dependencies and runs the STARK check, and an unavailable proof is not a consensus-invalidity condition.
/// </remarks>
public interface IBlockProofSidecarSource
{
    /// <summary>Records the proof of a block this node built, so its own import does not depend on peers.</summary>
    void RememberProduced(Block block);

    /// <summary>Returns the recursive STARK whose header hashes to <paramref name="block"/>'s hash, or null if unavailable.</summary>
    Task<RecursiveStark?> TryGetAsync(Block block, CancellationToken cancellationToken);
}
