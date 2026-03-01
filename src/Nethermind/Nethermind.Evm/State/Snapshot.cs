// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.InteropServices;

namespace Nethermind.Evm.State;

/// <summary>
/// Stores state and storage rollback positions.
/// For state snapshots, <see cref="StateSnapshot"/> is a frame id returned by <c>StateProvider.TakeSnapshot()</c>,
/// not a linear journal index.
/// <see cref="EmptyPosition"/> remains the external "no snapshot" marker and maps to frame id 0 in state provider.
/// </summary>
[StructLayout(LayoutKind.Auto)]
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
