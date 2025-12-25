// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.State.Flat.ScopeProvider;

public interface ITrieWarmer
{
    public void PushSlotJob(
        FlatWorldStateScope scope,
        FlatStorageTree storageTree,
        Address path,
        in UInt256? index,
        int sequenceId);

    public void PushAddressJob(
        FlatWorldStateScope scope,
        Address? path,
        int sequenceId);

    public void PushJobMulti(
        FlatWorldStateScope scope,
        Address? path,
        FlatStorageTree? storageTree,
        in UInt256? index,
        int sequenceId);

    void OnNewScope();
}

public class NoopTrieWarmer : ITrieWarmer
{
    public void PushSlotJob(FlatWorldStateScope scope, FlatStorageTree storageTree, Address path, in UInt256? index,
        int sequenceId)
    {
    }

    public void PushAddressJob(FlatWorldStateScope scope, Address? path, int sequenceId)
    {
    }

    public void PushJobMulti(FlatWorldStateScope scope, Address? path, FlatStorageTree? storageTree, in UInt256? index,
        int sequenceId)
    {
    }

    public void OnNewScope()
    {
    }
}
