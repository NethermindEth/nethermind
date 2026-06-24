// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac.Features.AttributeFilters;
using Nethermind.Crypto;
using Nethermind.Network.Config;
using Nethermind.Network.Enr;

namespace Nethermind.Network.Discovery;

public sealed class NodeRecordProvider(
    [KeyFilter(IProtectedPrivateKey.NodeKey)] IProtectedPrivateKey nodeKey,
    IIPResolver ipResolver,
    IEthereumEcdsa ethereumEcdsa,
    INetworkConfig networkConfig
) : INodeRecordProvider
{
    private readonly Lock _lock = new();
    private Task<NodeRecord>? _nodeRecordTask;

    public ValueTask<NodeRecord> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        Task<NodeRecord>? task = Volatile.Read(ref _nodeRecordTask);
        if (task is null)
        {
            lock (_lock)
            {
                // Build once, guarding concurrent callers (Ping/HandleEnrRequest run from concurrent
                // discovery handlers). Use CancellationToken.None so the cached ENR isn't faulted by a
                // single caller's token; per-call cancellation is honored via WaitAsync below.
                task = _nodeRecordTask ??= PrepareNodeRecord(CancellationToken.None);
            }
        }

        return new ValueTask<NodeRecord>(task.WaitAsync(cancellationToken));
    }

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
