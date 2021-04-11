//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Network;
using Nethermind.Network.Config;

namespace Nethermind.JsonRpc.Modules.Admin
{
    public class AdminRpcModule : IAdminRpcModule
    {
        private readonly IBlockTree _blockTree;
        private readonly INetworkConfig _networkConfig;
        private readonly IPeerManager _peerManager;
        private readonly IStaticNodesManager _staticNodesManager;
        private readonly IEnode _enode;
        private readonly string _dataDir;
        
        [NotNull]
        private NodeInfo _nodeInfo;

        public AdminRpcModule(
            IBlockTree blockTree,
            INetworkConfig networkConfig,
            IPeerManager peerManager,
            IStaticNodesManager staticNodesManager,
            IEnode enode,
            string dataDir)
        {
            _enode = enode ?? throw new ArgumentNullException(nameof(enode));
            _dataDir = dataDir ?? throw new ArgumentNullException(nameof(dataDir));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _peerManager = peerManager ?? throw new ArgumentNullException(nameof(peerManager));
            _networkConfig = networkConfig ?? throw new ArgumentNullException(nameof(networkConfig));
            _staticNodesManager = staticNodesManager ?? throw new ArgumentNullException(nameof(staticNodesManager));

            BuildNodeInfo();
        }

        private void BuildNodeInfo()
        {
            _nodeInfo = new NodeInfo();
            _nodeInfo.Name = ClientVersion.Description;
            _nodeInfo.Enode = _enode.Info;
            byte[] publicKeyBytes = _enode.PublicKey?.Bytes;
            _nodeInfo.Id = (publicKeyBytes == null ? Keccak.Zero : Keccak.Compute(publicKeyBytes)).ToString(false);
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
                _peerManager.AddPeer(new NetworkNode(enode));
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
                removed = _peerManager.RemovePeer(new NetworkNode(enode));
            }
            
            return removed
                ? ResultWrapper<string>.Success(enode)
                : ResultWrapper<string>.Fail("Failed to remove peer.");
        }
        
        public ResultWrapper<PeerInfo[]> admin_peers(bool includeDetails = false)
            => ResultWrapper<PeerInfo[]>.Success(
                _peerManager.ActivePeers.Select(p => new PeerInfo(p, includeDetails)).ToArray());

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
    }
}
