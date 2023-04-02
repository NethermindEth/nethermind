// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.FullPruning;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Network;
using Nethermind.Network.Config;
using Nethermind.Stats.Model;

namespace Nethermind.JsonRpc.Modules.Admin;

public class AdminRpcModule : IAdminRpcModule
{
    private readonly IBlockTree _blockTree;
    private readonly INetworkConfig _networkConfig;
    private readonly IPeerPool _peerPool;
    private readonly IStaticNodesManager _staticNodesManager;
    private readonly IEnode _enode;
    private readonly string _dataDir;
    private readonly ManualPruningTrigger _pruningTrigger;
    private NodeInfo _nodeInfo = null!;

    public AdminRpcModule(
        IBlockTree blockTree,
        INetworkConfig networkConfig,
        IPeerPool peerPool,
        IStaticNodesManager staticNodesManager,
        IEnode enode,
        string dataDir,
        ManualPruningTrigger pruningTrigger)
    {
        _enode = enode ?? throw new ArgumentNullException(nameof(enode));
        _dataDir = dataDir ?? throw new ArgumentNullException(nameof(dataDir));
        _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
        _peerPool = peerPool ?? throw new ArgumentNullException(nameof(peerPool));
        _networkConfig = networkConfig ?? throw new ArgumentNullException(nameof(networkConfig));
        _staticNodesManager = staticNodesManager ?? throw new ArgumentNullException(nameof(staticNodesManager));
        _pruningTrigger = pruningTrigger;

        BuildNodeInfo();
    }

    private void BuildNodeInfo()
    {
        _nodeInfo = new NodeInfo();
        _nodeInfo.Name = ProductInfo.ClientId;
        _nodeInfo.Enode = _enode.Info;
        byte[] publicKeyBytes = _enode.PublicKey?.Bytes;
        _nodeInfo.Id = publicKeyBytes is null ? Keccak.Zero.ToString(false) : ValueKeccak.Compute(publicKeyBytes).ToString(false);
        _nodeInfo.Ip = _enode.HostIp?.ToString();
        _nodeInfo.ListenAddress = $"{_enode.HostIp}:{_enode.Port}";
        _nodeInfo.Ports.Discovery = _networkConfig.DiscoveryPort;
        _nodeInfo.Ports.Listener = _networkConfig.P2PPort;
        UpdateEthProtocolInfo();
    }

    private void UpdateEthProtocolInfo()
    {
        _nodeInfo.Protocols["eth"].Difficulty = _blockTree.Head?.TotalDifficulty ?? 0;
        _nodeInfo.Protocols["eth"].ChainId = _blockTree.ChainId;
        _nodeInfo.Protocols["eth"].HeadHash = _blockTree.HeadHash;
        _nodeInfo.Protocols["eth"].GenesisHash = _blockTree.GenesisHash;
    }

    public async Task<ResultWrapper<string>> admin_addPeer(string enode, bool addToStaticNodes = false)
    {
        bool added;
        if (addToStaticNodes)
        {
            added = await _staticNodesManager.AddAsync(enode);
        }
        else
        {
            NetworkNode networkNode = new(enode);
            _peerPool.GetOrAdd(new Node(networkNode));
            added = true;
        }

        return added
            ? ResultWrapper<string>.Success(enode)
            : ResultWrapper<string>.Fail("Failed to add peer.");
    }

    public async Task<ResultWrapper<string>> admin_removePeer(string enode, bool removeFromStaticNodes = false)
    {
        bool removed;
        if (removeFromStaticNodes)
        {
            removed = await _staticNodesManager.RemoveAsync(enode);
        }
        else
        {
            removed = _peerPool.TryRemove(new NetworkNode(enode).NodeId, out Peer _);
        }

        return removed
            ? ResultWrapper<string>.Success(enode)
            : ResultWrapper<string>.Fail("Failed to remove peer.");
    }

    public ResultWrapper<PeerInfo[]> admin_peers(bool includeDetails = false)
        => ResultWrapper<PeerInfo[]>.Success(
            _peerPool.ActivePeers.Select(p => new PeerInfo(p.Value, includeDetails)).ToArray());

    public ResultWrapper<NodeInfo> admin_nodeInfo()
    {
        UpdateEthProtocolInfo();
        return ResultWrapper<NodeInfo>.Success(_nodeInfo);
    }

    public ResultWrapper<string> admin_dataDir()
    {
        return ResultWrapper<string>.Success(_dataDir);
    }

    public ResultWrapper<bool> admin_setSolc()
    {
        return ResultWrapper<bool>.Success(true);
    }

    public ResultWrapper<PruningStatus> admin_prune()
    {
        return ResultWrapper<PruningStatus>.Success(_pruningTrigger.Trigger());
    }
}
