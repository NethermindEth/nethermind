// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.PersistedSnapshots;

/// <summary>
/// Distinguishes between base persisted snapshots (containing actual data) and
/// compacted snapshots (merging multiple snapshots, may use NodeRef references).
/// </summary>
public enum PersistedSnapshotType : byte
{
    Base = 0,
    Compacted = 1,
}
