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

using Jint.Native;
using Nethermind.Core.Crypto;

namespace Nethermind.Cli.Modules
{
    [CliModule("proof")]
    public class ProofCliModule : CliModuleBase
    {
        [CliFunction("proof", "call")]
        public JsValue Call(object tx, string blockParameter = null)
        {
            return NodeManager.PostJint("proof_call", tx, blockParameter ?? "latest").Result;
        }

        [CliFunction("proof", "getTransactionReceipt")]
        public JsValue GetTransactionReceipt(string transactionHash, bool includeHeader)
        {
            return NodeManager.PostJint("proof_getTransactionReceipt", CliParseHash(transactionHash), includeHeader).Result;
        }

        [CliFunction("proof", "getTransactionByHash")]
        public JsValue GetTransactionByHash(string transactionHash, bool includeHeader)
        {
            return NodeManager.PostJint("proof_getTransactionByHash", CliParseHash(transactionHash), includeHeader).Result;
        }

        public ProofCliModule(ICliEngine cliEngine, INodeManager nodeManager) : base(cliEngine, nodeManager)
        {
        }
    }
}