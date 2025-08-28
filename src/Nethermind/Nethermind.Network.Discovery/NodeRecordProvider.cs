// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac.Features.AttributeFilters;
using System;
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

    private readonly object _lock = new();
    private readonly NodeRecordSigner _enrSigner = new(ethereumEcdsa, nodeKey.Unprotect());
    private readonly IBlockTree _blockTree = blockTree;
    private readonly IForkInfo _forkInfo = forkInfo;
    private readonly INetworkConfig _networkConfig = networkConfig;
    private readonly IIPResolver _ipResolver = ipResolver;
    private readonly byte[] _publicKey = nodeKey.CompressedPublicKey;

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

        // Subscribe for future updates so ENR reflects post-sync head
        _blockTree.NewHeadBlock += OnNewHeadBlock;

        return selfNodeRecord;
    }

    private void OnNewHeadBlock(object? sender, BlockEventArgs e)
    {
        // Recompute fork id on head changes and update ENR if it changed
        lock (_lock)
        {
            if (_nodeRecord is null) return;

            Nethermind.Network.Enr.ForkId? previous = _nodeRecord.GetObj<Nethermind.Network.Enr.ForkId>(EnrContentKey.Eth);
            ForkId newFork = GetCurrentForkId();

            if (previous is null || !previous.Value.ForkHash.AsSpan().SequenceEqual(newFork.HashBytes) || previous.Value.NextBlock != (long)newFork.Next)
            {
                _nodeRecord.SetEntry(new EthEntry(newFork.HashBytes, checked((long)newFork.Next)));
                _enrSigner.Sign(_nodeRecord);
            }
        }
    }

    private ForkId GetCurrentForkId()
    {
        var headHeader = _blockTree.BestSuggestedHeader ?? _blockTree.Genesis;
        long headNumber = headHeader?.Number ?? _blockTree.BestKnownNumber;
        ulong headTimestamp = headHeader?.Timestamp ?? 0UL;
        return _forkInfo.GetForkId(headNumber, headTimestamp);
    }

    private void UpdateEthEntry(NodeRecord record)
    {
        ForkId currentForkId = GetCurrentForkId();
        record.SetEntry(new EthEntry(currentForkId.HashBytes, checked((long)currentForkId.Next)));
    }
}
