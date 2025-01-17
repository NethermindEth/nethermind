// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using Nethermind.Serialization.Json;
using System.Linq;
using System.Collections.Generic;
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
using Nethermind.Synchronization.FastSync;

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
    private readonly ManualPruningTrigger _pruningTrigger;
    private readonly IBlockingVerifyTrie _blockingVerifyTrie;
    private readonly IStateReader _stateReader;
    private readonly IJsonSerializer _serializer;
    private NodeInfo _nodeInfo = null!;

    public AdminRpcModule(
        IBlockTree blockTree,
        INetworkConfig networkConfig,
        IPeerPool peerPool,
        IStaticNodesManager staticNodesManager,
        IBlockingVerifyTrie blockingVerifyTrie,
        IStateReader stateReader,
        IEnode enode,
        string dataDir,
        ManualPruningTrigger pruningTrigger,
        ChainParameters parameters,
        IJsonSerializer ethereumJsonSerializer)
    {
        _enode = enode ?? throw new ArgumentNullException(nameof(enode));
        _dataDir = dataDir ?? throw new ArgumentNullException(nameof(dataDir));
        _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
        _peerPool = peerPool ?? throw new ArgumentNullException(nameof(peerPool));
        _networkConfig = networkConfig ?? throw new ArgumentNullException(nameof(networkConfig));
        _staticNodesManager = staticNodesManager ?? throw new ArgumentNullException(nameof(staticNodesManager));
        _blockingVerifyTrie = blockingVerifyTrie ?? throw new ArgumentNullException(nameof(blockingVerifyTrie));
        _stateReader = stateReader ?? throw new ArgumentNullException(nameof(stateReader));
        _pruningTrigger = pruningTrigger;
        _parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
        _serializer = ethereumJsonSerializer ?? throw new ArgumentNullException(nameof(ethereumJsonSerializer));

        BuildNodeInfo();
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

    public ResultWrapper<PruningStatus> admin_prune()
    {
        return ResultWrapper<PruningStatus>.Success(_pruningTrigger.Trigger());
    }

    public ResultWrapper<string> admin_verifyTrie(BlockParameter block)
    {
        BlockHeader? header = _blockTree.FindHeader(block);
        if (header is null)
        {
            return ResultWrapper<string>.Fail("Unable to find block. Unable to know state root to verify.");
        }

        if (!_stateReader.HasStateForBlock(header))
        {
            return ResultWrapper<string>.Fail("Unable to start verify trie. State for block missing.");
        }

        if (!_blockingVerifyTrie.TryStartVerifyTrie(header))
        {
            return ResultWrapper<string>.Fail("Unable to start verify trie. Verify trie already running.");
        }

        return ResultWrapper<string>.Success("Starting.");
    }
    public async Task<ResultWrapper<bool>> admin_exportChain(string file, ulong first = 0, ulong last = 0)
{
    try
    {
        // Validate file path
        if (string.IsNullOrWhiteSpace(file))
        {
            return ResultWrapper<bool>.Fail("File path cannot be empty or whitespace.");
        }

        // Determine actual block range
        if (first == 0)
        {
            first = (ulong)(_blockTree.Genesis?.Number ?? 0);
        }
        //  If 'last' == 0, interpret that as the chain head.
        if (last == 0 || last > (ulong)(_blockTree.Head?.Number ?? 0))
        {
            last = (ulong)(_blockTree.Head?.Number ?? 0);
        }

        // Sanity check: ensure 'last' >= 'first'
        if (last < first)
        {
            return ResultWrapper<bool>.Fail($"Invalid block range. first({first}) > last({last}).");
        }

        List<Block> blocks = new();
        for (ulong i = first; i <= last; i++)
        {
            Block? block = _blockTree.FindBlock((long)i, BlockTreeLookupOptions.None);
            if (block == null)
            {
                return ResultWrapper<bool>.Fail($"Block not found at height {i}.");
            }
            blocks.Add(block);
        }

        string json = _serializer.Serialize(blocks);

        await File.WriteAllTextAsync(file, json);

        return ResultWrapper<bool>.Success(true);
    }
    catch (Exception ex)
    {
        return ResultWrapper<bool>.Fail($"Exception during export: {ex.Message}");
    }
}
}
