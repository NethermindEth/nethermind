// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
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
    private NodeRecord? _nodeRecord;

    public async ValueTask<NodeRecord> GetCurrentAsync(CancellationToken cancellationToken = default)
        => _nodeRecord ??= await PrepareNodeRecord(cancellationToken);

    private async Task<NodeRecord> PrepareNodeRecord(CancellationToken cancellationToken)
    {
        // TODO: Add forkid
        NodeRecord selfNodeRecord = new();
        selfNodeRecord.SetEntry(IdEntry.Instance);
        IIPResolver.NethermindIp ip = await ipResolver.Resolve(cancellationToken);
        selfNodeRecord.SetEntry(new IpEntry(ip.ExternalIp));
        selfNodeRecord.SetEntry(new TcpEntry(networkConfig.P2PPort));
        selfNodeRecord.SetEntry(new UdpEntry(networkConfig.DiscoveryPort));
        selfNodeRecord.SetEntry(new SecP256k1Entry(nodeKey.CompressedPublicKey));
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
