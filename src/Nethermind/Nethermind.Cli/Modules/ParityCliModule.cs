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

using Jint.Native;
using Nethermind.Core;

namespace Nethermind.Cli.Modules
{
    [CliModule("parity")]
    public class ParityCliModule : CliModuleBase
    {
        public ParityCliModule(ICliEngine engine, INodeManager nodeManager) : base(engine, nodeManager)
        {
        }
        
        [CliFunction("parity", "pendingTransactions", Description = "Returns the pending transactions using Parity format")]
        public JsValue PendingTransactions() => NodeManager.PostJint("parity_pendingTransactions").Result;

        [CliFunction("parity", "getBlockReceipts", Description = "Returns receipts from all transactions from particular block")]
        public JsValue GetBlockReceipts(string blockParameter) => NodeManager.PostJint("parity_getBlockReceipts", blockParameter).Result;
        
        [CliProperty("parity", "enode", Description = "Returns the node enode URI.")]
        public string? Enode() => NodeManager.Post<string>("parity_enode").Result;
        
        [CliFunction("parity", "clearEngineSigner", Description = "Clears an authority account for signing consensus messages. Blocks will not be sealed.")]
        public bool ClearSigner() => NodeManager.Post<bool>("parity_clearEngineSigner").Result;
        
        [CliFunction("parity", "setEngineSigner", Description = "Sets an authority account for signing consensus messages.")]
        public bool SetSigner(Address address, string password) => NodeManager.Post<bool>("parity_setEngineSigner", address, password).Result;
        
        [CliFunction("parity", "setEngineSignerSecret", Description = "Sets an authority account for signing consensus messages.")]
        public bool SetEngineSignerSecret(string privateKey) => NodeManager.Post<bool>("parity_setEngineSignerSecret", privateKey).Result;
        
        [CliProperty("parity", "netPeers", Description = "Returns connected peers. Peers with non-empty protocols have completed handshake.")]
        public JsValue NetPeers() => NodeManager.PostJint("parity_netPeers").Result;
    }
}
