//  Copyright (c) 2018 Demerzel Solutions Limited
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

using System.Threading.Tasks;

namespace Nethermind.JsonRpc.Modules.Admin
{
    [RpcModule(ModuleType.Admin)]
    public interface IAdminModule : IModule
    {
        [JsonRpcMethod(Description = "", Returns = "String", IsImplemented = true)]
        Task<ResultWrapper<string>> admin_addPeer(string enode, bool addToStaticNodes = false);
        
        [JsonRpcMethod(Description = "", Returns = "String", IsImplemented = true)]
        Task<ResultWrapper<string>> admin_removePeer(string enode, bool removeFromStaticNodes = false);
        
        [JsonRpcMethod(Description = "", Returns = "Array", IsImplemented = true)]
        ResultWrapper<PeerInfo[]> admin_peers();
        
        [JsonRpcMethod(Description = "Relevant information about this node", Returns = "Object", IsImplemented = true)]
        ResultWrapper<NodeInfo> admin_nodeInfo();
        
        [JsonRpcMethod(Description = "Base data directory path", Returns = "String", IsImplemented = false)]
        ResultWrapper<string> admin_dataDir();
        
        [JsonRpcMethod(Description = "[DEPRECATED]", IsImplemented = false)]
        ResultWrapper<bool> admin_setSolc();
    }
}