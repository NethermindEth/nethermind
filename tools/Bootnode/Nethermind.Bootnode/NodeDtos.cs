// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Bootnode;

internal sealed record NodeDto(
    string NodeId,
    string IdHash,
    string Host,
    int TcpPort,
    int DiscoveryPort,
    string? Enode,
    string? Enr,
    string Protocol,
    bool Active,
    bool IsBootnode,
    DateTimeOffset FirstSeenUtc,
    DateTimeOffset LastSeenUtc,
    int SeenCount);

internal sealed record BootnodeIdentity(string Enode, string Enr, ulong EnrSequence, string NodeId, string Address);

internal sealed record BootnodeStatus(
    BootnodeIdentity Identity,
    string[] Protocols,
    bool ActiveDiscovery,
    int DiscoveryPort,
    int HttpPort,
    int MetricsPort)
{
    public object CreateStatus(DiscoverySnapshot snapshot) => new
    {
        Identity,
        Protocols,
        ActiveDiscovery,
        DiscoveryPort,
        HttpPort,
        MetricsPort,
        Nodes = new
        {
            Active = snapshot.ActiveCount,
            All = snapshot.AllCount,
            ActiveByProtocol = new
            {
                Discv4 = snapshot.ActiveDiscv4Count,
                Discv5 = snapshot.ActiveDiscv5Count,
                Both = snapshot.ActiveBothCount,
                Configured = snapshot.ActiveConfiguredCount
            },
            AllByProtocol = new
            {
                Discv4 = snapshot.AllDiscv4Count,
                Discv5 = snapshot.AllDiscv5Count,
                Both = snapshot.AllBothCount,
                Configured = snapshot.AllConfiguredCount
            }
        }
    };
}
