// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.ScopeProvider;

/// <summary>A trie with queued speculative writes that one of the scope's speculation workers drains.</summary>
internal interface ISpeculativeTrie
{
    /// <summary>Drains the queued writes once; returns without touching the trie when finalization owns it.</summary>
    void RunSpeculationOnce();
}

/// <summary>
/// Ownership of a speculatively updated trie. The block thread moves it Idle to Queued when it enqueues a write, a
/// worker moves it Queued to Running to Idle around a drain, and finalization claims Idle or Queued as Owned, after
/// which no worker can touch the trie again.
/// </summary>
internal static class SpeculationState
{
    public const int Idle = 0;
    public const int Queued = 1;
    public const int Running = 2;
    public const int Owned = 3;
}
