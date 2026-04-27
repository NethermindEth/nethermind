// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.PersistedSnapshots;

/// <summary>
/// Distinguishes between full persisted snapshots (containing actual data) and
/// linked snapshots (merging multiple snapshots, all trie values are NodeRef references).
/// </summary>
public enum PersistedSnapshotType : byte
{
    Full = 0,
    Linked = 1,
}
