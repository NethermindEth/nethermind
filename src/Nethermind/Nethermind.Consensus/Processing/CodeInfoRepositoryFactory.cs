// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Evm;
using Nethermind.Evm.State;

namespace Nethermind.Consensus.Processing;

/// <summary>Builds the <see cref="ICodeInfoRepository"/> a <see cref="BlockAccessListManager"/> tx-processor uses over its world state.</summary>
public delegate ICodeInfoRepository CodeInfoRepositoryFactory(IWorldState worldState);

/// <summary>Standard <see cref="CodeInfoRepositoryFactory"/> choices.</summary>
public static class CodeInfoRepositoryFactories
{
    /// <summary>Caching repository for normal block processing; the DI default.</summary>
    public static readonly CodeInfoRepositoryFactory Caching =
        static worldState => new EthereumCodeInfoRepository(worldState);

    /// <summary>Non-caching repository for witness/stateless execution: the caching repo serves code from a process-wide static cache without touching the world state, leaving those accesses out of the witness.</summary>
    public static readonly CodeInfoRepositoryFactory Witness =
        static worldState => new CodeInfoRepository(worldState, new EthereumPrecompileProvider());
}
