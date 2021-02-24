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
        
        [CliFunction("admin", "peers",
            Description = "Displays a list of connected peers",
            ExampleRequest = "admin.peers",
            ExampleResponse = "Request complete in 627464.281μs\n[\n  {\n    \"clientId\": \"Geth/v1.9.15-stable-0f77f34b/linux-amd64/go1.14.4\",\n    \"host\": \"::ffff:47.218.109.107\",\n    \"port\": \"34182\",\n    \"address\": \"[::ffff:47.218.109.107]:34182\",\n    \"isBootnode\": false,\n    \"isTrusted\": false,\n    \"isStatic\": false,\n    \"enode\": \"enode://4df44eba60b4d2b9edadad10a4c4a9fb837e910932eb9b6aa5a90b3a99472af6e362ff2be5f45b5eca248521d87b42a461d119633e4856e291d304f93762821b@47.218.109.107:34182\"\n  }\n]")]
        public object[]? Peers(bool includeDetails = false) => NodeManager.Post<object[]>("admin_peers", includeDetails).Result;
        
        [CliProperty("admin", "nodeInfo")]
        public object? NodeInfo() => NodeManager.Post<object>("admin_nodeInfo").Result;
        
        [CliFunction("admin", "addPeer", 
            Description = "Adds given node to the static nodes",
            ExampleRequest = "admin.addPeer(\"enode://92c18bfbd45c9c7a8d46d5a766d7da4b6c1fac4f980cd11172738975e10cb84a4a98884affd240f4c40d98f371a7b2b8bd0e91c59c7beee20d20e4735a2af6e1@127.0.0.1:30001\", true)",
            ExampleResponse = "Request complete in 58129.371μs\n\"enode://92c18bfbd45c9c7a8d46d5a766d7da4b6c1fac4f980cd11172738975e10cb84a4a98884affd240f4c40d98f371a7b2b8bd0e91c59c7beee20d20e4735a2af6e1@127.0.0.1:30001\"")]
        public string? AddPeer(string enode, bool addToStaticNodes = false) => NodeManager.Post<string>("admin_addPeer", enode, addToStaticNodes).Result;
        
        [CliFunction("admin", "removePeer", 
            Description = "Removes given node from the static nodes",
            ExampleRequest = "admin.removePeer(\"enode://92c18bfbd45c9c7a8d46d5a766d7da4b6c1fac4f980cd11172738975e10cb84a4a98884affd240f4c40d98f371a7b2b8bd0e91c59c7beee20d20e4735a2af6e1@127.0.0.1:30001\", true)",
            ExampleResponse = "Request complete in 361680.159μs\n\"enode://92c18bfbd45c9c7a8d46d5a766d7da4b6c1fac4f980cd11172738975e10cb84a4a98884affd240f4c40d98f371a7b2b8bd0e91c59c7beee20d20e4735a2af6e1@127.0.0.1:30001\"")]
        public string? RemovePeer(string enode, bool removeFromStaticNodes = false) => NodeManager.Post<string>("admin_removePeer", enode, removeFromStaticNodes).Result;
    }
}
