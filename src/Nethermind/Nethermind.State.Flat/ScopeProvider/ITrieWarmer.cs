// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.State.Flat.ScopeProvider;

public interface ITrieWarmer
{
    public void PushSlotJob(
        IStorageWarmer storageTree,
        in UInt256? index,
        int sequenceId,
        bool isWrite);

    public void PushAddressJob(
        IAddressWarmer scope,
        Address? path,
        int sequenceId,
        bool isWrite);

    public void PushJobMulti(
        IAddressWarmer scope,
        Address? path,
        IStorageWarmer? storageTree,
        in UInt256? index,
        int sequenceId,
        bool isWrite);

    void OnEnterScope();
    void OnExitScope();

    public interface IAddressWarmer
    {
        bool WarmUpStateTrie(Address address, int sequenceId, bool isWrite);
    }

    public interface IStorageWarmer
    {
        bool WarmUpStorageTrie(UInt256 index, int sequenceId, bool isWrite);
    }
}
