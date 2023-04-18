// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.State
{
    /// <summary>
    /// Represents state that can be anchored at specific state root, snapshot, committed, reverted.
    /// Current format is an intermittent form on the way to a better state management.
    /// </summary>
    public interface IWorldState : IJournal<Snapshot>, IStateProvider
    {
        void ClearStorage(Address address);

        byte[] GetOriginal(in StorageCell storageCell);

        byte[] Get(in StorageCell storageCell);

        void Set(in StorageCell storageCell, byte[] newValue);

        byte[] GetTransientState(in StorageCell storageCell);

        void SetTransientState(in StorageCell storageCell, byte[] newValue);
        void Commit(IReleaseSpec releaseSpec, IWorldStateTracer? stateTracer, bool isGenesis = false);
        Snapshot TakeSnapshot(bool newTransactionStart = false);
        Snapshot IJournal<Snapshot>.TakeSnapshot() => TakeSnapshot();
    }
}
