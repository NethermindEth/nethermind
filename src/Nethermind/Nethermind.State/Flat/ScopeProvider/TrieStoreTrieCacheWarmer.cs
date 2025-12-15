// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.State.Flat.ScopeProvider;

public interface ITrieWarmer
{
    public void PushJob(
        FlatWorldStateScope scope,
        Address? path,
        FlatStorageTree? storageTree,
        in UInt256? index,
        int sequenceId);

    void OnNewScope();
}

public class NoopTrieWarmer : ITrieWarmer
{
    public void PushJob(FlatWorldStateScope scope, Address? path, FlatStorageTree? storageTree, in UInt256? index, int sequenceId)
    {
    }

    public void OnNewScope()
    {
    }
}
