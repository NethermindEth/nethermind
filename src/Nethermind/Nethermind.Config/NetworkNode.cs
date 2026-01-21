// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable
using Lantern.Discv5.Enr;
using Lantern.Discv5.Enr.Entries;
using Lantern.Discv5.Enr.Identity;
using Lantern.Discv5.Enr.Identity.V4;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;

namespace Nethermind.Config;

/// <summary>
/// Node data for storage and configuration only.
/// </summary>
public class NetworkNode
{
    private static readonly EnrFactory _enrFactory = new(new EnrEntryRegistry());
    private static readonly IIdentityVerifier identityVerifier = new IdentityVerifierV4();

    private readonly Enode? _enode;
    private readonly Enr? _enr;

    public bool IsEnode => _enode is not null;
    public bool IsEnr => _enr is not null;

    public NetworkNode(string nodeString)
    {
        if (Enode.IsEnode(nodeString, out _))
        {
            _enode = new Enode(nodeString);
        }
        else
        {
            _enr = _enrFactory.CreateFromString(nodeString, identityVerifier);
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

    public override string ToString() => IsEnode ? _enode!.ToString() : _enr!.ToString();

    public NetworkNode(PublicKey publicKey, string ip, int port, long reputation = 0)
    {
        _enode = new Enode(publicKey, IPAddress.Parse(ip), port);
        Reputation = reputation;
    }

    public NetworkNode(Enode enode) => _enode = enode;

    public Enode Enode => _enode ?? throw new InvalidDataException(nameof(Enode));
    public Enr Enr => _enr ?? throw new InvalidDataException(nameof(Enr));

    public PublicKey NodeId => IsEnode ? _enode!.PublicKey : new PublicKey(_enr!.GetEntry<EntrySecp256K1>(EnrEntryKey.Secp256K1).Value);
    public string Host => IsEnode ? _enode!.HostIp.ToString() : _enr!.GetEntry<EntryIp>(EnrEntryKey.Ip).Value.ToString();
    public IPAddress HostIp => IsEnode ? _enode!.HostIp : _enr!.GetEntry<EntryIp>(EnrEntryKey.Ip).Value;
    public int Port => IsEnode ? _enode!.Port : _enr!.GetEntry<EntryTcp>(EnrEntryKey.Tcp).Value;
    public long Reputation { get; set; }
}
