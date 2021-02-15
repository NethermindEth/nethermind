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
using Nethermind.JsonRpc.Data;

namespace Nethermind.Cli.Modules
{
    [CliModule("personal")]
    public class PersonalCliModule : CliModuleBase
    {
        [CliFunction("personal", "importRawKey")]
        public string? ImportRawKey(string keyData, string passphrase)
        {
            return NodeManager.Post<string>($"personal_importRawKey", keyData, passphrase).Result;
        }

        [CliProperty("personal", "listAccounts")]
        public JsValue ListAccounts()
        {
            return NodeManager.PostJint($"personal_listAccounts").Result;
        }

        [CliFunction("personal", "newAccount")]
        public string? NewAccount(string password)
        {
            return NodeManager.Post<string>($"personal_newAccount", password).Result;
        }
        
        [CliFunction("personal", "lockAccount")]
        public bool LockAccount(string addressHex)
        {
            return NodeManager.Post<bool>($"personal_lockAccount", addressHex).Result;
        }
        
        [CliFunction("personal", "unlockAccount")]
        public bool UnlockAccount(string addressHex, string password)
        {
            return NodeManager.Post<bool>($"personal_unlockAccount", addressHex, password).Result;
        }

        public PersonalCliModule(ICliEngine engine, INodeManager nodeManager) : base(engine, nodeManager)
        {
        }
    }
}
