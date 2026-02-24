// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.Storage;

/// <summary>
/// Physical location of a persisted snapshot within an arena file.
/// </summary>
public readonly record struct SnapshotLocation(int ArenaId, long Offset, int Size);
