// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO.Abstractions;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MathNet.Numerics.LinearAlgebra.Factorization;
using Nethermind.Blockchain;
using Nethermind.Blockchain.FullPruning;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Era1;
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
    private readonly IProcessExitToken _processExitToken;
    private NodeInfo _nodeInfo = null!;
    private readonly IEraExporter _eraExporter;

    public AdminRpcModule(
        IBlockTree blockTree,
        INetworkConfig networkConfig,
        IPeerPool peerPool,
        IStaticNodesManager staticNodesManager,
        IEnode enode,
        IEraExporter eraService,
        string dataDir,
        ManualPruningTrigger pruningTrigger,
        IProcessExitToken processExitToken)
    {
        _enode = enode ?? throw new ArgumentNullException(nameof(enode));
        _dataDir = dataDir ?? throw new ArgumentNullException(nameof(dataDir));
        _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
        _peerPool = peerPool ?? throw new ArgumentNullException(nameof(peerPool));
        _networkConfig = networkConfig ?? throw new ArgumentNullException(nameof(networkConfig));
        _staticNodesManager = staticNodesManager ?? throw new ArgumentNullException(nameof(staticNodesManager));
        _pruningTrigger = pruningTrigger;
        _processExitToken = processExitToken;
        _eraExporter = eraService;
        BuildNodeInfo();
    }

    private void BuildNodeInfo()
    {
        _nodeInfo = new NodeInfo();
        _nodeInfo.Name = ProductInfo.ClientId;
        _nodeInfo.Enode = _enode.Info;
        byte[] publicKeyBytes = _enode.PublicKey?.Bytes;
        _nodeInfo.Id = (publicKeyBytes is null ? Keccak.Zero : Keccak.Compute(publicKeyBytes)).ToString(false);
        _nodeInfo.Ip = _enode.HostIp?.ToString();
        _nodeInfo.ListenAddress = $"{_enode.HostIp}:{_enode.Port}";
        _nodeInfo.Ports.Discovery = _networkConfig.DiscoveryPort;
        _nodeInfo.Ports.Listener = _networkConfig.P2PPort;
        UpdateEthProtocolInfo();
    }

    private void UpdateEthProtocolInfo()
    {
        _nodeInfo.Protocols["eth"].Difficulty = _blockTree.Head?.TotalDifficulty ?? 0;
        _nodeInfo.Protocols["eth"].NewtorkId = _blockTree.ChainId;
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

    private Task _exportTask = Task.CompletedTask;
    private int _canEnter = 1;

    public Task<ResultWrapper<string>> admin_exportHistory(string destination, int epochFrom, int epochTo)
    {
        //TODO sanitize destination path
        if (epochFrom < 0 || epochTo < 0)
            return ResultWrapper<string>.Fail("Epoch number cannot be negative.");
        if (epochTo < epochFrom)
            return ResultWrapper<string>.Fail($"Invalid range {epochFrom}-{epochTo}.");

        //TODO what is the correct bounds check? Should canonical be required?
        Block? latestHead = _blockTree.Head;
        if (latestHead == null)
            return ResultWrapper<string>.Fail("Node is currently unable to export.");

        int from = epochFrom * EraWriter.MaxEra1Size;
        int to = epochTo * EraWriter.MaxEra1Size + EraWriter.MaxEra1Size - 1;
        long remainingInEpoch = EraWriter.MaxEra1Size - latestHead.Number % EraWriter.MaxEra1Size;
        long mostRecentFinishedEpoch = (latestHead.Number == 0 ? 0 : latestHead.Number / EraWriter.MaxEra1Size) - (remainingInEpoch == 0 ? 0 : 1);
        if (mostRecentFinishedEpoch < 0)
            return ResultWrapper<string>.Fail($"No epochs ready for export.");
        if (mostRecentFinishedEpoch < epochFrom || mostRecentFinishedEpoch < epochTo)
            return ResultWrapper<string>.Fail($"Cannot export beyond epoch {mostRecentFinishedEpoch}.");
        
        Block? earliest = _blockTree.FindBlock(from, BlockTreeLookupOptions.DoNotCreateLevelIfMissing);

        if (earliest == null)
            return ResultWrapper<string>.Fail($"Cannot export epoch {epochFrom}.");

        if (Interlocked.Exchange(ref _canEnter, 0) == 1 && _exportTask.IsCompleted)
        {
            try
            {
                _exportTask = _eraExporter.Export(destination, from, to, EraWriter.MaxEra1Size, _processExitToken.Token);
            }
            finally
            {
                Interlocked.Exchange(ref _canEnter, 1);
            }

            //TODO better message?
            return ResultWrapper<string>.Success("Started export task");
        }
        else
        {
            return ResultWrapper<string>.Fail("An export job is already running.");
        }
    }

    //TODO maybe move to cli?
    public Task<ResultWrapper<string>> admin_verifyHistory(string eraSource, string accumulatorFile)
    {
        try
        {
            if (Interlocked.Exchange(ref _canEnter, 0) == 1 && _exportTask.IsCompleted)
            {
                //TODO correct network 
                //_importExportTask = _eraService.VerifyEraFiles(eraSource);

                //TODO better message?
                return ResultWrapper<string>.Success("Started export task");
            }
            else
            {
                return ResultWrapper<string>.Fail("An export job is currently running.");
            }
        }
        finally
        {
            Interlocked.Exchange(ref _canEnter, 1);
        }
    }
}
