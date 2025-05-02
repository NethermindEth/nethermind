// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.FullPruning;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Network;
using Nethermind.Network.Config;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State;
using Nethermind.Stats.Model;
using Nethermind.JsonRpc.Modules.Subscribe;
using System.Text.Json;

namespace Nethermind.JsonRpc.Modules.Admin;

public class AdminRpcModule : IAdminRpcModule
{
    private readonly ChainParameters _parameters;
    private readonly IBlockTree _blockTree;
    private readonly INetworkConfig _networkConfig;
    private readonly IPeerPool _peerPool;
    private readonly IStaticNodesManager _staticNodesManager;
    private readonly IEnode _enode;
    private readonly string _dataDir;
    private readonly IStateReader _stateReader;
    private NodeInfo _nodeInfo = null!;
    private readonly ITrustedNodesManager _trustedNodesManager;
    private readonly ISubscriptionManager _subscriptionManager;

    public AdminRpcModule(
        IBlockTree blockTree,
        INetworkConfig networkConfig,
        IPeerPool peerPool,
        IStaticNodesManager staticNodesManager,
        IStateReader stateReader,
        IEnode enode,
        string dataDir,
        ChainParameters parameters,
        ITrustedNodesManager trustedNodesManager,
        ISubscriptionManager subscriptionManager)
    {
        _enode = enode ?? throw new ArgumentNullException(nameof(enode));
        _dataDir = dataDir ?? throw new ArgumentNullException(nameof(dataDir));
        _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
        _peerPool = peerPool ?? throw new ArgumentNullException(nameof(peerPool));
        _networkConfig = networkConfig ?? throw new ArgumentNullException(nameof(networkConfig));
        _staticNodesManager = staticNodesManager ?? throw new ArgumentNullException(nameof(staticNodesManager));
        _stateReader = stateReader ?? throw new ArgumentNullException(nameof(stateReader));
        _parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
        _trustedNodesManager = trustedNodesManager ?? throw new ArgumentNullException(nameof(trustedNodesManager));

        BuildNodeInfo();
        _subscriptionManager = subscriptionManager;
    }

    private void BuildNodeInfo()
    {
        _nodeInfo = new NodeInfo
        {
            Name = ProductInfo.ClientId,
            Enode = _enode.Info,
            Id = (_enode.PublicKey?.Hash ?? Keccak.Zero).ToString(false),
            Ip = _enode.HostIp?.ToString(),
            ListenAddress = $"{_enode.HostIp}:{_enode.Port}",
            Ports =
            {
                Discovery = _networkConfig.DiscoveryPort,
                Listener = _networkConfig.P2PPort
            }
        };

        UpdateEthProtocolInfo();
    }

    private void UpdateEthProtocolInfo()
    {
        _nodeInfo.Protocols["eth"].Difficulty = _blockTree.Head?.TotalDifficulty ?? 0;
        _nodeInfo.Protocols["eth"].NewtorkId = _blockTree.NetworkId;
        _nodeInfo.Protocols["eth"].ChainId = _blockTree.ChainId;
        _nodeInfo.Protocols["eth"].HeadHash = _blockTree.HeadHash;
        _nodeInfo.Protocols["eth"].GenesisHash = _blockTree.GenesisHash;
        _nodeInfo.Protocols["eth"].Config = _parameters;
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

    public async Task<ResultWrapper<bool>> admin_addTrustedPeer(string enode)
    {
        Enode enodeObj = new(enode);

        if (_trustedNodesManager.IsTrusted(enodeObj) || await _trustedNodesManager.AddAsync(enodeObj, updateFile: true))
        {
            _peerPool.GetOrAdd(new NetworkNode(enodeObj));
            return ResultWrapper<bool>.Success(true);
        }
        else
        {
            return ResultWrapper<bool>.Fail("Failed to add trusted peer.");
        }
    }

    public async Task<ResultWrapper<bool>> admin_removeTrustedPeer(string enode)
    {
        Enode enodeObj = new(enode);

        if (await _trustedNodesManager.RemoveAsync(enodeObj, updateFile: true))
        {
            return ResultWrapper<bool>.Success(true);
        }
        else
        {
            return ResultWrapper<bool>.Fail("Failed to remove trusted peer.");
        }
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

    public ResultWrapper<bool> admin_isStateRootAvailable(BlockParameter block)
    {
        BlockHeader? header = _blockTree.FindHeader(block);
        if (header is null)
        {
            return ResultWrapper<bool>.Fail("Unable to find block. Unable to know state root to verify.");
        }

        return ResultWrapper<bool>.Success(_stateReader.HasStateForBlock(header));
    }

    public ResultWrapper<string> admin_subscribe(string subscriptionName, string? args = null)
    {
        try
        {
            ResultWrapper<string> successfulResult = ResultWrapper<string>.Success(_subscriptionManager.AddSubscription(Context.DuplexClient, subscriptionName, args));
            return successfulResult;
        }
        catch (KeyNotFoundException)
        {
            return ResultWrapper<string>.Fail($"Wrong subscription type: {subscriptionName}.");
        }
        catch (ArgumentException e)
        {
            return ResultWrapper<string>.Fail($"Invalid params", ErrorCodes.InvalidParams, e.Message);
        }
        catch (JsonException)
        {
            return ResultWrapper<string>.Fail($"Invalid params", ErrorCodes.InvalidParams);
        }
    }

    public ResultWrapper<bool> admin_unsubscribe(string subscriptionId)
    {
        bool unsubscribed = _subscriptionManager.RemoveSubscription(Context.DuplexClient, subscriptionId);
        return unsubscribed
            ? ResultWrapper<bool>.Success(true)
            : ResultWrapper<bool>.Fail($"Failed to unsubscribe: {subscriptionId}.");
    }

    public JsonRpcContext Context { get; set; }
}
