// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain;

public interface IBlockhashCache
{
    Hash256? GetHash(BlockHeader headBlock, ulong depth);
    /// <summary>Loads the ancestor hashes of <paramref name="blockHeader"/>.</summary>
    /// <returns>The ancestor hashes, where index <c>i</c> holds the hash at depth <c>i + 1</c> (index 0 is the parent hash),
    /// or <see langword="null"/> when none are prefetched.</returns>
    Task<Hash256[]?> Prefetch(BlockHeader blockHeader, CancellationToken cancellationToken);

    /// <summary>Drops all cached block-hash chains.</summary>
    void Clear();
}
