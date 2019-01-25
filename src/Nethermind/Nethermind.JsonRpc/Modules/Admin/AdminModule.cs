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

using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Logging;

namespace Nethermind.JsonRpc.Modules.Admin
{
    public class AdminModule : ModuleBase, IAdminModule
    {
        public override ModuleType ModuleType => ModuleType.Admin;

        public ResultWrapper<PeerInfo[]> admin_addPeer()
        {
            throw new System.NotImplementedException();
        }

        public ResultWrapper<PeerInfo[]> admin_peers()
        {
            throw new System.NotImplementedException();
        }

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

        public AdminModule(IConfigProvider configProvider, ILogManager logManager, IJsonSerializer jsonSerializer) : base(configProvider, logManager, jsonSerializer)
        {
        }
    }
}