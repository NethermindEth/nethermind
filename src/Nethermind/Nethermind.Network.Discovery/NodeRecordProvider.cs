// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac.Features.AttributeFilters;
using System;
using Nethermind.Crypto;
using Nethermind.Network.Config;
using Nethermind.Network.Enr;
using Nethermind.Network;
using Nethermind.Core.Crypto;
using Nethermind.Blockchain;
using Nethermind.Core;

namespace Nethermind.Network.Discovery;

public class NodeRecordProvider(
    [KeyFilter(IProtectedPrivateKey.NodeKey)] IProtectedPrivateKey nodeKey,
    IIPResolver ipResolver,
    IEthereumEcdsa ethereumEcdsa,
    INetworkConfig networkConfig,
    IForkInfo forkInfo,
    IBlockTree blockTree
) : INodeRecordProvider
{
    private readonly NodeRecordSigner _enrSigner = new(ethereumEcdsa, nodeKey.Unprotect());
    private readonly IForkInfo _forkInfo = forkInfo;
    private readonly INetworkConfig _networkConfig = networkConfig;
    private readonly IIPResolver _ipResolver = ipResolver;
    private readonly CompressedPublicKey _publicKey = nodeKey.CompressedPublicKey;
    private readonly IBlockTree _blockTree = blockTree;
    private readonly object _updateLock = new();

    NodeRecord? _nodeRecord = null;
    public NodeRecord Current => _nodeRecord ??= InitializeNodeRecord();

    private NodeRecord InitializeNodeRecord()
    {
        NodeRecord selfNodeRecord = new();
        selfNodeRecord.SetEntry(IdEntry.Instance);
        selfNodeRecord.SetEntry(new IpEntry(_ipResolver.ExternalIp));
        selfNodeRecord.SetEntry(new TcpEntry(_networkConfig.P2PPort));
        selfNodeRecord.SetEntry(new UdpEntry(_networkConfig.DiscoveryPort));
        // Add eth forkid entry if blockchain context is available; otherwise defer until head updates
        if (_blockTree.Genesis is not null)
        {
            BlockHeader? latestHeader = _blockTree.FindLatestHeader();
            if (latestHeader is not null)
            {
                TryUpdateEthEntry(selfNodeRecord, latestHeader.Number, latestHeader.Timestamp);
            }
        }
        selfNodeRecord.SetEntry(new Secp256K1Entry(_publicKey));
        // Set a fresh sequence and sign
        selfNodeRecord.EnrSequence = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _enrSigner.Sign(selfNodeRecord);
        if (!_enrSigner.Verify(selfNodeRecord))
        {
            throw new NetworkingException("Self ENR initialization failed", NetworkExceptionType.Discovery);
        }

        // subscribe to head updates to refresh eth fork id in ENR
        _blockTree.NewHeadBlock += OnNewHeadBlock;

        return selfNodeRecord;
    }

    private void OnNewHeadBlock(object? sender, BlockEventArgs e)
    {
        UpdateCurrentRecordIfForkChanged(e.Block.Header.Number, e.Block.Header.Timestamp);
    }

    private void UpdateCurrentRecordIfForkChanged(long headNumber, ulong headTimestamp)
    {
        NodeRecord record = Current;
        lock (_updateLock)
        {
        // Compute new fork id and compare to existing ENR eth entry
        if (!TryComputeForkId(headNumber, headTimestamp, out ForkId computed))
        {
            return;
        }
            Nethermind.Network.Enr.ForkId? existing = record.GetValue<Nethermind.Network.Enr.ForkId>(EnrContentKey.Eth);
            bool changed = existing is null
                || existing.Value.NextBlock != (long)computed.Next
                || !existing.Value.ForkHash.AsSpan().SequenceEqual(computed.HashBytes);

            if (!changed)
            {
                return;
            }

            if (!TryUpdateEthEntry(record, headNumber, headTimestamp))
            {
                return;
            }
            // Bump ENR sequence using current milliseconds as requested
            record.EnrSequence = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _enrSigner.Sign(record);
        }
    }

    private bool TryUpdateEthEntry(NodeRecord record, long headNumber, ulong headTimestamp)
    {
        if (!TryComputeForkId(headNumber, headTimestamp, out ForkId currentForkId))
        {
            return false;
        }
        record.SetEntry(new EthEntry(currentForkId.HashBytes, checked((long)currentForkId.Next)));
        return true;
    }

    private bool TryComputeForkId(long headNumber, ulong headTimestamp, out ForkId forkId)
    {
        try
        {
            forkId = _forkInfo.GetForkId(headNumber, headTimestamp);
            return true;
        }
        catch
        {
            forkId = default;
            return false;
        }
    }
}
