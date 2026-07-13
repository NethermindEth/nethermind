// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.State.Flat.ScopeProvider;

public interface ITrieWarmer
{
    /// <param name="warmSiblings">When the hint is a slot deletion, also warm the deepest branch's
    /// other children — branch collapse resolves a surviving sibling that no read path touches.</param>
    public bool PushSlotJob(
        IStorageWarmer storageTree,
        in UInt256 index,
        int sequenceId,
        bool warmSiblings = false);

    /// <summary>
    /// Like <see cref="PushSlotJob"/>, but safe to call from multiple producer threads.
    /// Routes through the MPMC job buffer so background <c>HintBal</c> enqueuers do not violate the
    /// single-producer invariant of the main-thread slot buffer.
    /// </summary>
    public bool PushSlotJobMpmc(
        IStorageWarmer storageTree,
        in UInt256 index,
        int sequenceId,
        bool warmSiblings = false);

    public bool PushAddressJob(
        IAddressWarmer scope,
        Address? path,
        int sequenceId);

    void OnEnterScope();
    void OnExitScope();

    public interface IAddressWarmer
    {
        bool WarmUpStateTrie(Address address, int sequenceId);
    }

    public interface IStorageWarmer
    {
        bool WarmUpStorageTrie(UInt256 index, int sequenceId, bool warmSiblings = false);
    }
}
