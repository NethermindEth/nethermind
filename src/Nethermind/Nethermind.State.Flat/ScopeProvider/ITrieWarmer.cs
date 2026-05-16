// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.State.Flat.ScopeProvider;

public interface ITrieWarmer
{
    public bool PushSlotJob(
        IStorageWarmer storageTree,
        in UInt256? index,
        int sequenceId);

    public bool PushAddressJob(
        IAddressWarmer scope,
        Address? path,
        int sequenceId);

    void OnEnterScope();
    void OnExitScope();

    /// <summary>
    /// Pause worker threads to free CPU for tx processing. Jobs remain queued.
    /// </summary>
    void Pause() { }

    /// <summary>
    /// Resume worker threads to drain queued jobs before merkle phase.
    /// </summary>
    void Resume() { }

    public interface IAddressWarmer
    {
        bool WarmUpStateTrie(Address address, int sequenceId);
    }

    public interface IStorageWarmer
    {
        bool WarmUpStorageTrie(UInt256 index, int sequenceId);
    }
}
