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
        public static readonly Snapshot Empty = new(EmptyPosition,EmptyPosition);

        public Snapshot(int stateSnapshot, int storageSnapshot)
        {
            StateSnapshot = stateSnapshot;
            StorageSnapshot = storageSnapshot;
        }

        public int StateSnapshot { get; }
        public int StorageSnapshot { get; }

        public const int EmptyPosition = -1;
    }
}
