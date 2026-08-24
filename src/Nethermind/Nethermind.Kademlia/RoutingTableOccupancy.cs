// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Kademlia;

/// <summary>
/// Summary of how filled a routing table is.
/// </summary>
/// <param name="NodeCount">Number of nodes held across all buckets.</param>
/// <param name="Capacity">Number of node slots the current buckets provide, that is bucket count times k.</param>
public readonly record struct RoutingTableOccupancy(int NodeCount, int Capacity);
