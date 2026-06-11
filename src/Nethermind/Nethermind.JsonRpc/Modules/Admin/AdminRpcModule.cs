// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Network;
using Nethermind.Network.Config;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.JsonRpc.Modules.Subscribe;
using System.Text.Json;
using Nethermind.State;
using Nethermind.Network.Contract.P2P;


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
    private readonly IJsonRpcConfig _jsonRpcConfig;
    private readonly IBlockProcessingPauseControl _blockProcessingPauseControl;

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
        ISubscriptionManager subscriptionManager,
        IJsonRpcConfig jsonRpcConfig,
        IBlockProcessingPauseControl blockProcessingPauseControl)
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
        _subscriptionManager = subscriptionManager ?? throw new ArgumentNullException(nameof(subscriptionManager));
        _jsonRpcConfig = jsonRpcConfig ?? throw new ArgumentNullException(nameof(jsonRpcConfig));
        _blockProcessingPauseControl = blockProcessingPauseControl ?? throw new ArgumentNullException(nameof(blockProcessingPauseControl));

        BuildNodeInfo();
    }

    public ResultWrapper<bool> admin_pauseBlockProcessing()
    {
        _blockProcessingPauseControl.Pause();
        return ResultWrapper<bool>.Success(_blockProcessingPauseControl.IsPaused);
    }

    public ResultWrapper<bool> admin_resumeBlockProcessing()
    {
        _blockProcessingPauseControl.Resume();
        return ResultWrapper<bool>.Success(!_blockProcessingPauseControl.IsPaused);
    }

    public ResultWrapper<bool> admin_isBlockProcessingPaused() =>
        ResultWrapper<bool>.Success(_blockProcessingPauseControl.IsPaused);

    public async Task<ResultWrapper<bool>> admin_addPeer(string enode, bool persistent = false)
    {
        if (TryParseAsNetworkNode(enode, out NetworkNode? networkNode) is { } error) return error;
        using CancellationTokenSource timeout = BuildTimeoutCancellationTokenSource();

        await _staticNodesManager.AddAsync(networkNode!, updateFile: persistent, timeout.Token);
        return ResultWrapper<bool>.Success(true);
    }

    public async Task<ResultWrapper<bool>> admin_removePeer(string enode, bool persistent = false)
    {
        if (TryParseAsNetworkNode(enode, out NetworkNode? networkNode) is { } error) return error;
        using CancellationTokenSource timeout = BuildTimeoutCancellationTokenSource();

        try
        {
            await _staticNodesManager.RemoveAsync(networkNode!, updateFile: persistent, timeout.Token);
        }
        finally
        {
            _peerPool.TryRemove(networkNode!.NodeId, out _);
        }
        return ResultWrapper<bool>.Success(true);
    }

    public async Task<ResultWrapper<bool>> admin_addTrustedPeer(string enode, bool persistent = false)
    {
        if (TryParseAsEnode(enode, out Enode? enodeObj) is { } error) return error;

        if (_trustedNodesManager.IsTrusted(enodeObj!))
        {
            return ResultWrapper<bool>.Success(true);
        }

        using CancellationTokenSource timeout = BuildTimeoutCancellationTokenSource();

        try
        {
            await _trustedNodesManager.AddAsync(enodeObj!, updateFile: persistent, timeout.Token);
        }
        finally
        {
            _peerPool.GetOrAdd(new NetworkNode(enodeObj!));
        }
        return ResultWrapper<bool>.Success(true);
    }

    public async Task<ResultWrapper<bool>> admin_removeTrustedPeer(string enode, bool persistent = false)
    {
        if (TryParseAsEnode(enode, out Enode? enodeObj) is { } error) return error;
        using CancellationTokenSource timeout = BuildTimeoutCancellationTokenSource();

        await _trustedNodesManager.RemoveAsync(enodeObj!, updateFile: persistent, timeout.Token);
        return ResultWrapper<bool>.Success(true);
    }

    public ResultWrapper<PeerInfo[]> admin_peers(bool includeDetails = false)
    {
        PeerInfo[] validatedPeers = _peerPool.ActivePeers
            .Where(p => IsValidatedPeer(p.Value))
            .Select(p => new PeerInfo(p.Value, includeDetails))
            .ToArray();

        return ResultWrapper<PeerInfo[]>.Success(validatedPeers);
    }

    public ResultWrapper<NodeInfo> admin_nodeInfo()
    {
        UpdateEthProtocolInfo();
        return ResultWrapper<NodeInfo>.Success(_nodeInfo);
    }

    public ResultWrapper<string> admin_dataDir() => ResultWrapper<string>.Success(_dataDir);

    public ResultWrapper<bool> admin_setSolc() => ResultWrapper<bool>.Success(true);

    public ResultWrapper<bool> admin_isStateRootAvailable(BlockParameter block)
    {
        BlockHeader? header = _blockTree.FindHeader(block);
        if (header is null)
        {
            return ResultWrapper<bool>.Fail("Unable to find block. Unable to know state root to verify.");
        }

        return ResultWrapper<bool>.Success(_stateReader.HasStateForBlock(header!));
    }

    public ResultWrapper<string> admin_subscribe(string subscriptionName, string? args = null)
    {
        if (Subscription.ValidateArgs(args) is { } failure) return failure;

        try
        {
            ResultWrapper<string> successfulResult = ResultWrapper<string>.Success(_subscriptionManager.AddSubscription(Context.DuplexClient!, subscriptionName, args));
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
        bool unsubscribed = _subscriptionManager.RemoveSubscription(Context.DuplexClient!, subscriptionId);
        return unsubscribed
            ? ResultWrapper<bool>.Success(true)
            : ResultWrapper<bool>.Fail($"Failed to unsubscribe: {subscriptionId}.");
    }

    public JsonRpcContext Context { get; set; } = null!;

    private CancellationTokenSource BuildTimeoutCancellationTokenSource() => _jsonRpcConfig.BuildTimeoutCancellationToken();

    private static bool IsValidatedPeer(Peer peer) => peer.InSession?.IsNetworkIdMatched == true ||
                                                      peer.OutSession?.IsNetworkIdMatched == true;

    private static ResultWrapper<bool>? TryParseAsNetworkNode(string enode, out NetworkNode? node)
    {
        try
        {
            node = new NetworkNode(enode);
            return null;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or SocketException)
        {
            node = null;
            return ResultWrapper<bool>.Fail($"invalid enode: {ex.Message}", ErrorCodes.InvalidParams);
        }
    }

    private static ResultWrapper<bool>? TryParseAsEnode(string enode, out Enode? parsed)
    {
        try
        {
            parsed = new Enode(enode);
            return null;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or SocketException)
        {
            parsed = null;
            return ResultWrapper<bool>.Fail($"invalid enode: {ex.Message}", ErrorCodes.InvalidParams);
        }
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
        _nodeInfo.Protocols[Protocol.Eth].Difficulty = _blockTree.Head?.TotalDifficulty ?? 0;
        _nodeInfo.Protocols[Protocol.Eth].NetworkId = _blockTree.NetworkId;
        _nodeInfo.Protocols[Protocol.Eth].ChainId = _blockTree.ChainId;
        _nodeInfo.Protocols[Protocol.Eth].HeadHash = _blockTree.HeadHash;
        _nodeInfo.Protocols[Protocol.Eth].GenesisHash = _blockTree.GenesisHash;
        _nodeInfo.Protocols[Protocol.Eth].Config = _parameters;
    }
}
