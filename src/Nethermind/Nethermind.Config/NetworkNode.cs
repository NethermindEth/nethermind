// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Enr;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Net;

namespace Nethermind.Config;

/// <summary>
/// Node data for storage and configuration only.
/// </summary>
public class NetworkNode
{
    private readonly Enode? _enode;
    private readonly NodeRecord? _enr;

    [MemberNotNullWhen(true, nameof(Enode))]
    [MemberNotNullWhen(false, nameof(Enr))]
    public bool IsEnode => _enode is not null;

    [MemberNotNullWhen(true, nameof(Enr))]
    [MemberNotNullWhen(false, nameof(Enode))]
    public bool IsEnr => _enr is not null;

    public NetworkNode(string nodeString)
    {
        if (Enode.IsEnode(nodeString, out _))
        {
            _enode = new Enode(nodeString);
        }
        else
        {
            _enr = NodeRecord.FromEnrString(nodeString);
        }
    }

    public static NetworkNode[] ParseNodes(string? nodeRecords, ILogger logger)
    {
        if (nodeRecords is null)
        {
            return [];
        }

        string[] nodeStrings = nodeRecords.Split(",", StringSplitOptions.RemoveEmptyEntries);

        return ParseNodes(nodeStrings, logger);
    }

    public static NetworkNode[] ParseNodes(string[]? nodeRecords, ILogger logger)
    {
        if (nodeRecords is null)
        {
            return [];
        }

        List<NetworkNode> nodes = new(nodeRecords.Length);

        foreach (string nodeString in nodeRecords)
        {
            try
            {
                nodes.Add(new NetworkNode(nodeString.Trim()));
            }
            catch (Exception e)
            {
                if (logger.IsError) logger.Error($"Could not parse enode data from {nodeString}", e);
            }
        }

        return [.. nodes];
    }

    public override string ToString() => IsEnode ? Enode.ToString() : Enr.ToString();

    public NetworkNode(PublicKey publicKey, string ip, int port, long reputation = 0)
        : this(new Enode(publicKey, IPAddress.Parse(ip), port)) => Reputation = reputation;

    public NetworkNode(Enode enode) => _enode = enode;

    public Enode? Enode => _enode;

    public NodeRecord? Enr => _enr;

    public PublicKey NodeId => IsEnode ? Enode.PublicKey : GetEnrPublicKey();
    public string Host => IsEnode ? Enode.HostIp.ToString() : HostIp.ToString();
    public IPAddress HostIp => IsEnode ? Enode.HostIp : Enr!.DiscoveryIp ?? IPAddress.None;
    public int Port => IsEnode ? Enode.Port : Enr!.TcpPort ?? 0;
    public int DiscoveryPort => IsEnode ? Enode.DiscoveryPort : Enr!.DiscoveryPort ?? 0;
    public long Reputation { get; set; }

    private PublicKey GetEnrPublicKey()
    {
        CompressedPublicKey publicKey = Enr!.GetObj<CompressedPublicKey>(EnrContentKey.SecP256k1)
            ?? throw new InvalidOperationException("ENR is missing secp256k1 public key.");

        return publicKey.Decompress();
    }
}
