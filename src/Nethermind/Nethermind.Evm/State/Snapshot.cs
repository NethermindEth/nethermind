// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.State;

/// <summary>
/// Stores state and storage snapshots (as the change index that we can revert to)
/// At the beginning and after each commit the snapshot is set to the value of EmptyPosition
/// </summary>
public readonly struct Snapshot
{
    public Snapshot(in Storage storageSnapshot, int stateSnapshot, int balSnapshot)
    {
        StorageSnapshot = storageSnapshot;
        StateSnapshot = stateSnapshot;
        BlockAccessListSnapshot = balSnapshot;
    }

    public Snapshot(in Storage storageSnapshot, int stateSnapshot)
    {
        StorageSnapshot = storageSnapshot;
        StateSnapshot = stateSnapshot;
        BlockAccessListSnapshot = 0;
    }

    public static readonly Snapshot Empty = new(Storage.Empty, EmptyPosition, EmptyPosition);

    /// <summary>
    /// Tracks snapshot positions for Persistent and Transient storage
    /// </summary>
    public readonly struct Storage(int storageSnapshot, int transientStorageSnapshot)
    {
        public static readonly Storage Empty = new(EmptyPosition, EmptyPosition);

        public int PersistentStorageSnapshot { get; } = storageSnapshot;
        public int TransientStorageSnapshot { get; } = transientStorageSnapshot;
    }

    public Storage StorageSnapshot { get; }
    public int StateSnapshot { get; }
    public int BlockAccessListSnapshot { get; }

    public const int EmptyPosition = -1;
}

