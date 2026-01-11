// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.State.Flat.ScopeProvider;

public interface ITrieWarmer
{
    public void PushSlotJob(
        FlatStorageTree storageTree,
        Address path,
        in UInt256? index,
        int sequenceId,
        bool isWrite);

    public void PushAddressJob(
        FlatWorldStateScope scope,
        Address? path,
        int sequenceId,
        bool isWrite);

    public void PushJobMulti(
        FlatWorldStateScope scope,
        Address? path,
        FlatStorageTree? storageTree,
        in UInt256? index,
        int sequenceId,
        bool isWrite);

    void OnNewScope();
    void WaitUntilEmpty();
}

public class NoopTrieWarmer : ITrieWarmer
{
    public void PushSlotJob(FlatStorageTree storageTree, Address path, in UInt256? index,
        int sequenceId,
        bool isWrite)
    {
    }

    public void PushAddressJob(FlatWorldStateScope scope, Address? path, int sequenceId,
        bool isWrite)
    {
    }

    public void PushJobMulti(FlatWorldStateScope scope, Address? path, FlatStorageTree? storageTree, in UInt256? index,
        int sequenceId,
        bool isWrite)
    {
    }

    public void OnNewScope()
    {
    }

    public void WaitUntilEmpty()
    {
    }
}
