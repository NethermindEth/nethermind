// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.State;

/// <summary>
/// Stores state and storage snapshots (as the change index that we can revert to)
/// At the beginning and after each commit the snapshot is set to the value of EmptyPosition
/// </summary>
public readonly struct Snapshot(in Snapshot.Storage storageSnapshot, int stateSnapshot)
{
    public static readonly Snapshot Empty = new(Storage.Empty, EmptyPosition);

    /// <summary>
    /// Tracks snapshot positions for Persistent and Transient storage
    /// </summary>
    public readonly struct Storage(int storageSnapshot, int transientStorageSnapshot)
    {
        public static readonly Storage Empty = new(EmptyPosition, EmptyPosition);

        public int PersistentStorageSnapshot { get; } = storageSnapshot;
        public int TransientStorageSnapshot { get; } = transientStorageSnapshot;
    }

    public Storage StorageSnapshot { get; } = storageSnapshot;
    public int StateSnapshot { get; } = stateSnapshot;

    public const int EmptyPosition = -1;
}

