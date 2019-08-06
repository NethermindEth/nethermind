/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Logging;
using Nethermind.Network;

namespace Nethermind.JsonRpc.Modules.Admin
{
    public class AdminModule : IAdminModule
    {
        private readonly ILogger _logger;
        private readonly IPeerManager _peerManager;
        private readonly IStaticNodesManager _staticNodesManager;

        public AdminModule(ILogManager logManager, IPeerManager peerManager, IStaticNodesManager staticNodesManager)
        {
            _logger = logManager.GetClassLogger();
            _peerManager = peerManager ?? throw new ArgumentNullException(nameof(peerManager));
            _staticNodesManager = staticNodesManager ?? throw new ArgumentNullException(nameof(staticNodesManager));
        }
        
        public async Task<ResultWrapper<string>> admin_addPeer(string enode)
        {
            var added = await _staticNodesManager.AddAsync(enode);

            return added
                ? ResultWrapper<string>.Success(enode)
                : ResultWrapper<string>.Fail("Static node already exists.");
        }

        public async Task<ResultWrapper<string>> admin_removePeer(string enode)
        {
            var removed = await _staticNodesManager.RemoveAsync(enode);

            return removed
                ? ResultWrapper<string>.Success(enode)
                : ResultWrapper<string>.Fail("Static node was not found.");
        }

        public ResultWrapper<PeerInfo[]> admin_peers()
            => ResultWrapper<PeerInfo[]>.Success(_peerManager.ActivePeers.Select(p => new PeerInfo(p)).ToArray());

        public ResultWrapper<PeerInfo[]> admin_nodeInfo()
        {
            throw new System.NotImplementedException();
        }

        public ResultWrapper<PeerInfo[]> admin_dataDir()
        {
            throw new System.NotImplementedException();
        }

        public ResultWrapper<PeerInfo[]> admin_setSolc()
        {
            throw new System.NotImplementedException();
        }
    }
}