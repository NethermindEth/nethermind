// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State;

/// <summary>
/// Describes the historical state availability of a world-state implementation.
/// Used by <c>eth_capabilities</c> to report which historical state lookups the node can serve,
/// independently of the underlying storage strategy (trie pruning, flat DB, etc.).
/// </summary>
/// <param name="Archive">
/// True for a node that retains the full historical state from genesis.
/// </param>
/// <param name="RetentionWindowBlocks">
/// When non-null, the node maintains a rolling window of this many recent blocks of state.
/// Null when the node is archive (use <see cref="Archive"/>) or when retention is non-linear
/// (e.g. periodic full pruning where no fixed lower bound can be reported).
/// </param>
/// <param name="StateProofsSupported">
/// True when the node can serve trie-node-by-hash lookups required for <c>eth_getProof</c>.
/// </param>
public readonly record struct StateAvailability(
    bool Archive,
    long? RetentionWindowBlocks,
    bool StateProofsSupported);
