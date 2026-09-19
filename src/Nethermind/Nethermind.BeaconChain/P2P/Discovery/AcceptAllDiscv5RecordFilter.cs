// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.Discovery.Discv5.Kademlia;
using Nethermind.Network.Enr;

namespace Nethermind.BeaconChain.P2P.Discovery;

/// <summary>Excludes nothing: beacon chain peers are selected by <c>eth2</c> fork digest downstream, not at the discv5 layer.</summary>
internal sealed class AcceptAllDiscv5RecordFilter : IDiscv5RecordFilter
{
    public static AcceptAllDiscv5RecordFilter Instance { get; } = new();

    public bool Excludes(NodeRecord record) => false;
}
