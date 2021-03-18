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

namespace Nethermind.Cli.Modules
{
    [CliModule("admin")]
    public class AdminCliModule : CliModuleBase
    {
        public AdminCliModule(ICliEngine engine, INodeManager nodeManager) : base(engine, nodeManager)
        {
        }
        
        [CliFunction("admin", "peers", Description = "Displays a list of connected peers")]
        public object[]? Peers(bool includeDetails = false) => NodeManager.Post<object[]>("admin_peers", includeDetails).Result;
        
        [CliProperty("admin", "nodeInfo")]
        public object? NodeInfo() => NodeManager.Post<object>("admin_nodeInfo").Result;
        
        [CliFunction("admin", "addPeer", Description = "Adds given node to the static nodes")]
        public string? AddPeer(string enode, bool addToStaticNodes = false) => NodeManager.Post<string>("admin_addPeer", enode, addToStaticNodes).Result;
        
        [CliFunction("admin", "removePeer", Description = "Removes given node from the static nodes")]
        public string? RemovePeer(string enode, bool removeFromStaticNodes = false) => NodeManager.Post<string>("admin_removePeer", enode, removeFromStaticNodes).Result;
    }
}
