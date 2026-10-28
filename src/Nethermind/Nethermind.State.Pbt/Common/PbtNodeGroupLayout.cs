// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Pbt;

namespace Nethermind.State.Pbt.Common;

/// <summary>Splits node groups into the top groups nearly every block touches and the groups below them.</summary>
public static class PbtNodeGroupLayout
{
    /// <summary>Account groups keyed at or above this depth are top groups: the last level before the 16^8 dense band.</summary>
    public const int AccountTopDepth = 28;
    /// <summary>Code and storage groups keyed at or above this depth are top groups: every group keyed shorter than zone and address hash.</summary>
    public const int StemTopDepth = 260;

    /// <summary>Whether <paramref name="groupKey"/> is a top group, the root included.</summary>
    public static bool IsTopGroup<TPath>(TPath groupKey) where TPath : struct, IPbtNodePath<TPath> =>
        groupKey.BitDepth <= (PbtPartitions.PartitionOfPath(groupKey) == PbtPartition.Account ? AccountTopDepth : StemTopDepth);
}
