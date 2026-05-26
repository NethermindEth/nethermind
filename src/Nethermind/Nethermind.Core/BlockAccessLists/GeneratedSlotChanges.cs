// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Int256;

namespace Nethermind.Core.BlockAccessLists;

/// <summary>
/// Per-slot changes accumulated across all transactions that touched the slot. Backed by a
/// simple <see cref="List{T}"/> because changes are appended in increasing index order during
/// merging, so the list is naturally sorted and indexed access by position is sufficient for
/// RLP encoding.
/// </summary>
public class GeneratedSlotChanges(UInt256 key) : IComparable<GeneratedSlotChanges>
{
    public UInt256 Key { get; } = key;

    public List<StorageChange> Changes { get; } = [];

    public int CompareTo(GeneratedSlotChanges? other) => other is null ? 1 : Key.CompareTo(other.Key);
}
