// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.Stats.Model;

namespace Nethermind.Stats;

internal static class NodeStatsAdmission
{
    internal static bool TryGetStats(this INodeStatsManager manager, Node node, [NotNullWhen(true)] out INodeStats? stats)
    {
        stats = manager.GetStatsOrNull(node);
        return stats is not null;
    }
}
