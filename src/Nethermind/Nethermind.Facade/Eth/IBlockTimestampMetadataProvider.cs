// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Facade.Eth;

/// <summary>Resolves authenticated millisecond timestamp metadata for an RPC block.</summary>
/// <remarks>
/// A block lookup is keyed by its exact hash so metadata from a same-height reorganisation cannot be reused for
/// another block. A missing body returns <see langword="null"/> without caching the miss, allowing a later lookup to
/// observe a body that has since been downloaded.
/// </remarks>
public interface IBlockTimestampMetadataProvider
{
    /// <summary>Gets the authenticated millisecond timestamp for a loaded block.</summary>
    /// <param name="block">The block whose complete body is inspected.</param>
    /// <returns>The wrapped millisecond timestamp, or <see langword="null"/> when the body has no valid metadata.</returns>
    ulong? GetTimestampMs(Block block);

    /// <summary>Loads an exact block body and gets its authenticated millisecond timestamp.</summary>
    /// <param name="blockHash">The exact block hash to load.</param>
    /// <param name="blockNumber">The block number used to accelerate the lookup.</param>
    /// <returns>The wrapped millisecond timestamp, or <see langword="null"/> when the body is unavailable or invalid.</returns>
    ulong? GetTimestampMs(Hash256 blockHash, ulong blockNumber);
}
