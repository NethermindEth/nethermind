// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Pbt;

/// <summary>Physical grouping used to store opaque canonical path/node records.</summary>
/// <remarks>
/// A record is always identified by the complete <see cref="PbtNodePath.Encode"/> result and contains
/// the exact canonical node codec bytes. Layout changes placement only; they never alter paths, nodes,
/// or root computation.
/// </remarks>
public enum PbtNodeLayout : byte
{
    /// <summary>Stores one canonical path/node record per physical key.</summary>
    Record = 0,

    /// <summary>Stores the same records in fixed groups selected by the first path-identity hash byte.</summary>
    HashBucket = 1,
}
