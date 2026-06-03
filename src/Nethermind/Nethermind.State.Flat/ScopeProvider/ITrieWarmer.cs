// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.State.Flat.ScopeProvider;

public interface ITrieWarmer
{
    public bool PushSlotJob(
        IStorageWarmer storageTree,
        in UInt256 index,
        int sequenceId);

    /// <summary>
    /// Like <see cref="PushSlotJob"/>, but safe to call from multiple producer threads.
    /// Routes through the MPMC job buffer so background <c>HintBal</c> enqueuers do not violate the
    /// single-producer invariant of the main-thread slot buffer.
    /// </summary>
    public bool PushSlotJobMpmc(
        IStorageWarmer storageTree,
        in UInt256 index,
        int sequenceId);

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
        bool WarmUpStorageTrie(UInt256 index, int sequenceId);

        /// <summary>
        /// Batched <see cref="WarmUpStorageTrie" /> for several slots of this storage tree, coalescing the
        /// underlying reads into one MultiGet. <paramref name="jobCount" /> is the number of enqueued jobs
        /// this batch represents (used to balance the outstanding-warmup accounting); it equals
        /// <paramref name="indices" />.Length.
        /// </summary>
        void WarmUpStorageTrieBatch(ReadOnlySpan<UInt256> indices, int sequenceId, int jobCount);
    }
}
