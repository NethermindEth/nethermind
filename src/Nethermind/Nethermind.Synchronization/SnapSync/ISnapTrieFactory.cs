// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.State.Snap;

namespace Nethermind.Synchronization.SnapSync;

public interface ISnapTrieFactory
{
    // Called once at the start/end of a snap-sync run from SnapSyncRunner.Run — sequential, no concurrent invocations.
    void EnsureInitialize() { }
    void FinalizeSync() { }

    ISnapTree<PathWithAccount> CreateStateTree();
    ISnapTree<PathWithStorageSlot> CreateStorageTree(in ValueHash256 accountPath);
}
