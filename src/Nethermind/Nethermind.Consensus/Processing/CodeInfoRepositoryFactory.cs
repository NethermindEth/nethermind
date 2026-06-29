// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Evm;
using Nethermind.Evm.State;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// Builds the <see cref="ICodeInfoRepository"/> a <see cref="BlockAccessListManager"/> tx-processor uses
/// over its (traced) world state: the caching repository for normal block processing, a non-caching one
/// for witness/stateless execution.
/// </summary>
/// <remarks>
/// A named delegate (rather than a bare <c>Func&lt;IWorldState, ICodeInfoRepository&gt;</c>) so the DI
/// registration is unambiguous and does not collide with Autofac's built-in parameterized-Func relationship.
/// </remarks>
public delegate ICodeInfoRepository CodeInfoRepositoryFactory(IWorldState worldState);

/// <summary>Standard <see cref="CodeInfoRepositoryFactory"/> choices.</summary>
public static class CodeInfoRepositoryFactories
{
    /// <summary>Caching repository for normal block processing. The DI default (see <c>BlockProcessingModule</c>).</summary>
    public static readonly CodeInfoRepositoryFactory Caching =
        static worldState => new EthereumCodeInfoRepository(worldState);

    /// <summary>
    /// Non-caching repository for witness/stateless execution, so every code access reads through the
    /// (traced) world state. The caching repository serves code from a process-wide static cache without
    /// touching the world state, which would leave those accesses out of the generated witness.
    /// </summary>
    public static readonly CodeInfoRepositoryFactory Witness =
        static worldState => new CodeInfoRepository(worldState, new EthereumPrecompileProvider());
}
