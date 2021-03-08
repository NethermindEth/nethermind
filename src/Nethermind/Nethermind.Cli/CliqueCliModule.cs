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
using Nethermind.Cli.Modules;
using Nethermind.Core;

namespace Nethermind.Cli
{
    [CliModule("clique")]
    public class CliqueCliModule : CliModuleBase
    {
        [CliFunction("clique", "getSnapshot")]
        public JsValue GetSnapshot()
        {
            return NodeManager.PostJint("clique_getSnapshot").Result;
        }

        [CliFunction("clique", "getSnapshotAtHash")]
        public JsValue GetSnapshotAtHash(string hash)
        {
            return NodeManager.PostJint("clique_getSnapshotAtHash", CliParseHash(hash)).Result;
        }
        
        [CliFunction("clique", "getSigners")]
        public JsValue GetSigners()
        {
            return NodeManager.PostJint("clique_getSigners").Result;
        }

        [CliFunction("clique", "getSignersAtNumber")]
        public JsValue GetSignersAtNumber(long number)
        {
            return NodeManager.PostJint("clique_getSignersAtNumber", number.ToString()).Result;
        }
        
        [CliFunction("clique", "getSignersAtHash")]
        public JsValue GetSignersAtHash(string hash)
        {
            return NodeManager.PostJint("clique_getSignersAtHash", CliParseHash(hash)).Result;
        }
        
        [CliFunction("clique", "getSignersAnnotated")]
        public JsValue GetSignersAnnotated()
        {
            return NodeManager.PostJint("clique_getSignersAnnotated").Result;
        }
        
        [CliFunction("clique", "getSignersAtHashAnnotated")]
        public JsValue GetSignersAtHashAnnotated(string hash)
        {
            return NodeManager.PostJint("clique_getSignersAtHashAnnotated", CliParseHash(hash)).Result;
        }
        
        [CliFunction("clique", "propose")]
        public bool Propose(string address, bool vote)
        {
            return NodeManager.Post<bool>("clique_propose", CliParseAddress(address), vote).Result;
        }
        
        [CliFunction("clique", "discard")]
        public bool Discard(string address)
        {
            return NodeManager.Post<bool>("clique_discard", CliParseAddress(address)).Result;
        }
        
        [CliFunction("clique", "produceBlock")]
        public bool ProduceBlock(string parentHash)
        {
            return NodeManager.Post<bool>("clique_produceBlock", CliParseHash(parentHash)).Result;
        }
        
        [CliFunction("clique", "getBlockSigner")]
        public Address? GetBlockSigner(string hash)
        {
            return NodeManager.Post<Address>("clique_getBlockSigner", CliParseHash(hash)).Result;
        }
        
        public CliqueCliModule(ICliEngine cliEngine, INodeManager nodeManager) : base(cliEngine, nodeManager)
        {
        }
    }
}
