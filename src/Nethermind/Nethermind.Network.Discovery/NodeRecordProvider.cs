// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac.Features.AttributeFilters;
using Nethermind.Crypto;
using Nethermind.Network.Config;
using Nethermind.Network.Enr;

namespace Nethermind.Network.Discovery;

public class NodeRecordProvider(
    [KeyFilter(IProtectedPrivateKey.NodeKey)] IProtectedPrivateKey nodeKey,
    IIPResolver ipResolver,
    IEthereumEcdsa ethereumEcdsa,
    INetworkConfig networkConfig
) : INodeRecordProvider
{

    NodeRecord? _nodeRecord = null;
    public NodeRecord Current => _nodeRecord ??= PrepareNodeRecord();

    private NodeRecord PrepareNodeRecord()
    {
        // TODO: Add forkid
        NodeRecord selfNodeRecord = new();
        selfNodeRecord.SetEntry(IdEntry.Instance);
        selfNodeRecord.SetEntry(new IpEntry(ipResolver.ExternalIp));
        selfNodeRecord.SetEntry(new TcpEntry(networkConfig.P2PPort));
        selfNodeRecord.SetEntry(new UdpEntry(networkConfig.DiscoveryPort));
        selfNodeRecord.SetEntry(new Secp256K1Entry(nodeKey.CompressedPublicKey));
        selfNodeRecord.EnrSequence = 1;
        NodeRecordSigner enrSigner = new(ethereumEcdsa, nodeKey.Unprotect());
        enrSigner.Sign(selfNodeRecord);
        if (!enrSigner.Verify(selfNodeRecord))
        {
            throw new NetworkingException("Self ENR initialization failed", NetworkExceptionType.Discovery);
        }

        return selfNodeRecord;
    }
}
