// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.State.Flat.ScopeProvider;

public interface ITrieWarmer
{
    /// <param name="isDelete">The slot is being set to zero; see <see cref="IStorageWarmer.WarmUpStorageTrie"/>.</param>
    public bool PushSlotJob(
        IStorageWarmer storageTree,
        in UInt256 index,
        int sequenceId,
        bool isDelete);

    /// <summary>
    /// Like <see cref="PushSlotJob"/>, but safe to call from multiple producer threads.
    /// Routes through the MPMC job buffer so background <c>HintBal</c> enqueuers do not violate the
    /// single-producer invariant of the main-thread slot buffer.
    /// </summary>
    public bool PushSlotJobMpmc(
        IStorageWarmer storageTree,
        in UInt256 index,
        int sequenceId,
        bool isDelete);

    public bool PushAddressJob(
        IAddressWarmer scope,
        Address? path,
        int sequenceId);

    /// <summary>
    /// <c>false</c> when every push is rejected, so callers can skip preparing warm-up jobs at all.
    /// </summary>
    /// <remarks>Override to <c>false</c> only if every <c>Push*</c> method always returns <c>false</c>.</remarks>
    bool IsActive => true;

    void OnEnterScope();
    void OnExitScope();

    public interface IAddressWarmer
    {
        bool WarmUpStateTrie(Address address, int sequenceId);
    }

    public interface IStorageWarmer
    {
        /// <param name="index">The slot whose trie path is warmed.</param>
        /// <param name="sequenceId">The hint sequence the job was queued under; a stale one is skipped.</param>
        /// <param name="isDelete">
        /// The slot is being set to zero, so its leaf will be removed. Removing a leaf can collapse its branch
        /// into the one child left, which the path alone does not load.
        /// </param>
        bool WarmUpStorageTrie(UInt256 index, int sequenceId, bool isDelete);
    }
}
