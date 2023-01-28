// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.State
{
    public class WorldState : IWorldState
    {
        public IStateProvider StateProvider { get; }

        public Snapshot TakeSnapshot(bool newTransactionStart = false)
        {
            Snapshot.Storage storageSnapshot = StorageProvider.TakeSnapshot(newTransactionStart);
            return new(StateProvider.TakeSnapshot(), storageSnapshot);
        }

        public void Restore(Snapshot snapshot)
        {
            StateProvider.Restore(snapshot.StateSnapshot);
            StorageProvider.Restore(snapshot.StorageSnapshot);
        }

        public IStorageProvider StorageProvider { get; }

        public WorldState(IStateProvider stateProvider, IStorageProvider storageProvider)
        {
            StateProvider = stateProvider ?? throw new ArgumentNullException(nameof(stateProvider));
            StorageProvider = storageProvider ?? throw new ArgumentNullException(nameof(storageProvider));
        }
    }
}
