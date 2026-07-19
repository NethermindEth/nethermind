// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Pbt;

namespace Nethermind.State.Pbt;

/// <summary>One tree leaf to fold into the rebuilt tree.</summary>
/// <remarks>
/// Entries reach <see cref="PbtRebuilder"/> in ascending tree-key order: the importer emits the
/// account zone, then the code zone, then the storage zone, and reads each from a source already
/// ordered by that zone's stems. Ordering is a cost property rather than a correctness one — the fold
/// tolerates any order — but it is what keeps each rebuild window a contiguous stem range, so a stem
/// is folded once instead of being read-modify-written by every window holding one of its leaves.
/// </remarks>
public readonly record struct RebuildEntry(Stem Stem, byte SubIndex, ValueHash256 Leaf);
