// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.State
{
    /// <summary>
    /// Represents state that can be anchored at specific state root, snapshot, committed, reverted.
    /// Current format is an intermittent form on the way to a better state management.
    /// </summary>
    public interface IWorldState : IJournal<Snapshot>, IStateProvider, IStorageProvider
    {
        IStorageProvider StorageProvider { get; }

        IStateProvider StateProvider { get; }

        new Snapshot TakeSnapshot(bool newTransactionStart = false);

        Snapshot IJournal<Snapshot>.TakeSnapshot() => TakeSnapshot();
        new void Reset();
        new void Commit(IStorageTracer stateTracer);
        new void Commit();
        new void Commit(IReleaseSpec releaseSpec, bool isGenesis = false);

        void Commit(IReleaseSpec releaseSpec, IWorldStateTracer stateTracer, bool isGenesis = false);
    }
}
