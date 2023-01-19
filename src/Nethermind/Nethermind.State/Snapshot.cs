// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Nethermind.Evm.Test")]
namespace Nethermind.State
{
    /// <summary>
    /// Stores state and storage snapshots (as the change index that we can revert to)
    /// At the beginning and after each commit the snapshot is set to the value of EmptyPosition
    /// </summary>
    public readonly struct Snapshot
    {
        public static readonly Snapshot Empty = new(EmptyPosition, Storage.Empty);

        public Snapshot(int stateSnapshot, Storage storageSnapshot)
        {
            StateSnapshot = stateSnapshot;
            StorageSnapshot = storageSnapshot;
        }

        /// <summary>
        /// Tracks snapshot positions for Persistent and Transient storage
        /// </summary>
        public readonly struct Storage
        {
            public static readonly Storage Empty = new(EmptyPosition, EmptyPosition);

            public int PersistentStorageSnapshot { get; }
            public int TransientStorageSnapshot { get; }

            public Storage(int storageSnapshot, int transientStorageSnapshot)
            {
                PersistentStorageSnapshot = storageSnapshot;
                TransientStorageSnapshot = transientStorageSnapshot;
            }
        }

        public int StateSnapshot { get; }
        public Storage StorageSnapshot { get; }

        public const int EmptyPosition = -1;
    }
}
