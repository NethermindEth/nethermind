// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning;

public interface IFinalizedStateProvider
{
    ulong FinalizedBlockNumber { get; }
    Hash256? GetFinalizedStateRootAt(ulong blockNumber);

    /// <summary>The block the chain currently follows, or null when no head has been chosen yet.</summary>
    /// <remarks>
    /// Distinct from the last block executed: an engine client can execute payloads it never selects, so
    /// state bounding that keys off execution history alone would drop the state the head still serves.
    /// </remarks>
    BlockHeader? Head => null;
}
