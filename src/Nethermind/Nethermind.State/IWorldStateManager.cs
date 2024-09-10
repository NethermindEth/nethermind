// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Trie.Pruning;

namespace Nethermind.State;

public interface IWorldStateManager : IPreBlockCaches
{
    IWorldStateProvider GlobalWorldStateProvider { get; }

    /// <summary>
    /// Used by read only tasks that need to execute blocks.
    /// </summary>
    /// <returns></returns>
    IWorldStateProvider CreateResettableWorldStateProvider();

    event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached;

    bool ClearCache();
}
