// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Pbt.Persistence;

/// <summary>The persisted node-group key layout, fixed when the pbt database is created.</summary>
public enum PbtNodeGroupKeyLayout
{
    /// <summary>The group path zero-padded to the column's full-key length, then its nibble count. A group sorts immediately before its descendants.</summary>
    Padded,

    /// <summary>The group path bytes, then 0 for a byte-aligned path or 1 for a nibble-aligned one. Keys are shorter; a byte-aligned group sorts before its descendants, a nibble-aligned one inside their range.</summary>
    Variable,
}
