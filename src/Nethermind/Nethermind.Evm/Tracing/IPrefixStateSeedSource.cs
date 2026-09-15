// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Evm.State;

namespace Nethermind.Evm.Tracing;

/// <summary>Supplies the state a block's transactions wrote before a given one, so that a trace of that transaction
/// can read it in place of replaying the transactions ahead of it.</summary>
public interface IPrefixStateSeedSource
{
    /// <summary>Whether this source can ever arm a slot; a read path is only prepared for an overlay when it can.</summary>
    bool Enabled { get; }

    /// <summary>Arms <paramref name="slot"/> with an overlay of everything the transactions before
    /// <paramref name="transactionIndex"/> wrote; reads of the scope in flight then see it ahead of the parent state.
    /// False leaves the slot untouched and means the caller replays the prefix as it always did.</summary>
    bool TrySeed(Block block, int transactionIndex, StateReadOverlaySlot slot);
}

public sealed class NullPrefixStateSeedSource : IPrefixStateSeedSource
{
    public static readonly NullPrefixStateSeedSource Instance = new();

    private NullPrefixStateSeedSource()
    {
    }

    public bool Enabled => false;

    public bool TrySeed(Block block, int transactionIndex, StateReadOverlaySlot slot) => false;
}
