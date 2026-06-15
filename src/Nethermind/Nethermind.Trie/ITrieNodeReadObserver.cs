// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Trie;

/// <summary>
/// Side-channel observer for raw trie node reads on the live block-processing path. Trie-store
/// adapters consult it where they resolve persisted nodes (e.g. during state-root recomputation,
/// where branch collapse resolves sibling nodes that never surface at the world-state level).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IsActive"/> lets call sites skip RLP materialization entirely when nothing is
/// recording, keeping the disarmed cost to a property read per node access.
/// </para>
/// <para>
/// Generic by design: it names what it does (observe node reads), not who uses it. Currently fired
/// only by the flat backend's trie adapters — flat's live read path is not an <see cref="Pruning.ITrieStore"/>,
/// so it cannot be decorated; the patricia backend captures the equivalent reads via a
/// <c>WitnessCapturingTrieStore</c> <see cref="Pruning.ITrieStore"/> decorator instead.
/// </para>
/// </remarks>
public interface ITrieNodeReadObserver
{
    bool IsActive { get; }

    void OnTrieNodeRead(Hash256 hash, byte[] rlp);
}
