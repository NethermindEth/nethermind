// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Trie.Pruning;

namespace Nethermind.State;

public interface IWorldStateManager
{
    IWorldState GlobalWorldState { get; }
    IStateReader GlobalStateReader { get; }

    /// <summary>
    /// Used by read only tasks that need to execute blocks.
    /// Why does it need an `IStateReader`? I'm not sure, but I don't wanna break things.
    /// The Action here is a resetter. Previously an explicit DbProvider's read only implementation need to be reset.
    /// </summary>
    /// <returns></returns>
    (IWorldState, IStateReader, Action) CreateResettableWorldState();
    event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached;
}
