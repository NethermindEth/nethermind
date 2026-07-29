// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain;

/// <summary>
/// Decides the BLOCKHASH result for an in-window ancestor whose hash cannot be resolved from the cache.
/// </summary>
public interface IUnresolvedBlockhashPolicy
{
    /// <returns>The fallback hash, or <c>null</c> so the EVM pushes 0; may instead throw to signal an invariant violation.</returns>
    Hash256? Resolve(BlockHeader currentBlock, ulong number);
}
