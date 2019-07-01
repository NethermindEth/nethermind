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

using Jint.Native;

namespace Nethermind.Cli.Modules
{
    [CliModule("personal")]
    public class PersonalCliModule : CliModuleBase
    {
        [CliProperty("personal", "listAccounts")]
        public JsValue ListAccounts()
        {
            return NodeManager.PostJint($"personal_listAccounts").Result;
        }

        [CliFunction("personal", "newAccount")]
        public JsValue NewAccount(string password)
        {
            return NodeManager.PostJint($"personal_newAccount", password).Result;
        }
        
        [CliFunction("personal", "lockAccount")]
        public JsValue LockAccount(string addressHex)
        {
            return NodeManager.PostJint($"personal_lockAccount", addressHex).Result;
        }
        
        [CliFunction("personal", "unlockAccount")]
        public JsValue UnlockAccount(string addressHex, string password)
        {
            return NodeManager.PostJint($"personal_unlockAccount", addressHex, password).Result;
        }

        public PersonalCliModule(ICliEngine engine, INodeManager nodeManager) : base(engine, nodeManager)
        {
        }
    }
}