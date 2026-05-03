// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.State.Snap;

namespace Nethermind.Synchronization.SnapSync;

public interface ISnapTrieFactory
{
    /// <summary>
    /// Called once at the start of a snap-sync run, before any tree is created. Implementations
    /// that need a clean slate (e.g. flat-state, which can't merge two snap-sync runs in place)
    /// perform their reset here. Called from a single sequential entry point — implementations
    /// must not assume concurrent invocations.
    /// </summary>
    void EnsureInitialize() { }

    /// <summary>
    /// Called once at the end of a snap-sync run, after all created trees have been disposed.
    /// Implementations that buffer writes can flush here. Called from a single sequential exit
    /// point — implementations must not assume concurrent invocations.
    /// </summary>
    void FinalizeSync() { }

    ISnapTree<PathWithAccount> CreateStateTree();
    ISnapTree<PathWithStorageSlot> CreateStorageTree(in ValueHash256 accountPath);
}
