// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac.Features.AttributeFilters;
using System;
using Nethermind.Crypto;
using Nethermind.Network.Config;
using Nethermind.Network.Enr;
using Nethermind.Network;
using Nethermind.Core.Crypto;

namespace Nethermind.Network.Discovery;

public class NodeRecordProvider(
    [KeyFilter(IProtectedPrivateKey.NodeKey)] IProtectedPrivateKey nodeKey,
    IIPResolver ipResolver,
    IEthereumEcdsa ethereumEcdsa,
    INetworkConfig networkConfig,
    IForkInfo forkInfo
) : INodeRecordProvider
{
    private readonly NodeRecordSigner _enrSigner = new(ethereumEcdsa, nodeKey.Unprotect());
    private readonly IForkInfo _forkInfo = forkInfo;
    private readonly INetworkConfig _networkConfig = networkConfig;
    private readonly IIPResolver _ipResolver = ipResolver;
    private readonly CompressedPublicKey _publicKey = nodeKey.CompressedPublicKey;

    NodeRecord? _nodeRecord = null;
    public NodeRecord Current => _nodeRecord ??= InitializeNodeRecord();

    private NodeRecord InitializeNodeRecord()
    {
        NodeRecord selfNodeRecord = new();
        selfNodeRecord.SetEntry(IdEntry.Instance);
        selfNodeRecord.SetEntry(new IpEntry(_ipResolver.ExternalIp));
        selfNodeRecord.SetEntry(new TcpEntry(_networkConfig.P2PPort));
        selfNodeRecord.SetEntry(new UdpEntry(_networkConfig.DiscoveryPort));
        // Add eth forkid entry based on current head
        UpdateEthEntry(selfNodeRecord);
        selfNodeRecord.SetEntry(new Secp256K1Entry(_publicKey));
        _enrSigner.Sign(selfNodeRecord);
        if (!_enrSigner.Verify(selfNodeRecord))
        {
            throw new NetworkingException("Self ENR initialization failed", NetworkExceptionType.Discovery);
        }

        return selfNodeRecord;
    }

    private void UpdateEthEntry(NodeRecord record)
    {
        // Simple timestamp-based forkId as per suggestion
        ulong now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        ForkId currentForkId = _forkInfo.GetForkId(0L, now);
        record.SetEntry(new EthEntry(currentForkId.HashBytes, checked((long)currentForkId.Next)));
    }
}
