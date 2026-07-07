// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Evm.State;

/// <summary>
/// Receives trie warm-up hints produced by speculative (prewarm) execution so the commit-path trie nodes
/// of the main block-processing scope can be loaded ahead of the final commit.
/// </summary>
/// <remarks>
/// Implemented by the main processing world-state scope (flat DB) and registered in
/// <see cref="PreBlockCaches.TrieHintSink"/> for the scope's lifetime. Hints are advisory and deduplicated
/// by the implementation; they are pushed concurrently from prewarm worker threads, so implementations must
/// be thread-safe and tolerate hints racing with commit (stale hints are dropped).
/// </remarks>
public interface IPrewarmTrieHintSink
{
    /// <summary>Hints that <paramref name="address"/> was touched, so its account-trie path is worth warming.</summary>
    void HintAccountWarm(Address address);

    /// <summary>Hints that the slot <paramref name="index"/> of <paramref name="address"/> was speculatively written, so its storage-trie path is worth warming.</summary>
    void HintSlotWarm(Address address, in UInt256 index);
}
