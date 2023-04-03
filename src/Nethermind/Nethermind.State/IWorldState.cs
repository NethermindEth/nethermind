// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.State
{
    /// <summary>
    /// Represents state that can be anchored at specific state root, snapshot, committed, reverted.
    /// Current format is an intermittent form on the way to a better state management.
    /// </summary>
    public interface IWorldState : IJournal<Snapshot>
    {
        IStorageProvider StorageProvider { get; }

        IStateProvider StateProvider { get; }

        Snapshot TakeSnapshot(bool newTransactionStart = false);

        Snapshot IJournal<Snapshot>.TakeSnapshot() => TakeSnapshot();
    }
}
