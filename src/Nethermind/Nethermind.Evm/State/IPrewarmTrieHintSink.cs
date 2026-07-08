// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Evm.State;

/// <summary>
/// Receives trie warm-up hints from speculative (prewarm) execution so the main scope's commit-path
/// trie nodes load ahead of the final commit. Registered in <see cref="PreBlockCaches.TrieHintSink"/>;
/// hints are advisory, deduplicated, and pushed concurrently from prewarm worker threads.
/// </summary>
public interface IPrewarmTrieHintSink
{
    void HintAccountWarm(Address address);

    void HintSlotWarm(Address address, in UInt256 index);
}
