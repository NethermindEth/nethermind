// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac.Features.AttributeFilters;
using Nethermind.Crypto;
using Nethermind.Network.Config;
using Nethermind.Network.Enr;
using Nethermind.Blockchain;
using Nethermind.Network;

namespace Nethermind.Network.Discovery;

public class NodeRecordProvider(
    [KeyFilter(IProtectedPrivateKey.NodeKey)] IProtectedPrivateKey nodeKey,
    IIPResolver ipResolver,
    IEthereumEcdsa ethereumEcdsa,
    INetworkConfig networkConfig,
    IBlockTree blockTree,
    IForkInfo forkInfo
) : INodeRecordProvider
{

    NodeRecord? _nodeRecord = null;
    public NodeRecord Current => _nodeRecord ??= PrepareNodeRecord();

    private NodeRecord PrepareNodeRecord()
    {
        NodeRecord selfNodeRecord = new();
        selfNodeRecord.SetEntry(IdEntry.Instance);
        selfNodeRecord.SetEntry(new IpEntry(ipResolver.ExternalIp));
        selfNodeRecord.SetEntry(new TcpEntry(networkConfig.P2PPort));
        selfNodeRecord.SetEntry(new UdpEntry(networkConfig.DiscoveryPort));
        // Add eth forkid entry based on current head
        var headHeader = blockTree.BestSuggestedHeader ?? blockTree.Genesis;
        long headNumber = headHeader?.Number ?? blockTree.BestKnownNumber;
        ulong headTimestamp = headHeader?.Timestamp ?? 0UL;
        var currentForkId = forkInfo.GetForkId(headNumber, headTimestamp);
        selfNodeRecord.SetEntry(new EthEntry(currentForkId.HashBytes, checked((long)currentForkId.Next)));
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
