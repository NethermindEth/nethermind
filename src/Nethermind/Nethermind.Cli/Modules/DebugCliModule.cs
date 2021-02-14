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

namespace Nethermind.Cli.Modules
{
    [CliModule("debug")]
    public class DebugCliModule : CliModuleBase
    {
        [CliFunction("debug", "getChainLevel")]
        public JsValue GetChainLevel(long number)
        {
            return NodeManager.PostJint("debug_getChainLevel", number).Result;
        }

        // [CliFunction("debug", "deleteChainSlice")]
        // public JsValue DeleteChainSlice(long startNumber)
        // {
        //     return NodeManager.PostJint("debug_deleteChainSlice", startNumber).Result;
        // }
        
        // [CliFunction("debug", "resetHead")]
        // public JsValue ResetHead(string blockHash)
        // {
        //     return NodeManager.PostJint("debug_resetHead", CliParseHash(blockHash)).Result;
        // }

        [CliFunction("debug", "traceBlock")]
        public JsValue TraceBlock(string rlp, object options)
        {
            return NodeManager.PostJint("debug_traceBlock", rlp, options).Result;
        }

        [CliFunction("debug", "traceBlockByNumber")]
        public JsValue TraceBlockByNumber(string number, object options)
        {
            return NodeManager.PostJint("debug_traceBlockByNumber", number, options).Result;
        }

        [CliFunction("debug", "traceBlockByHash")]
        public JsValue TraceBlockByHash(string hash, object options)
        {
            return NodeManager.PostJint("debug_traceBlockByHash", hash, options).Result;
        }

        [CliFunction("debug", "traceTransaction")]
        public JsValue TraceTransaction(string hash, object options)
        {
            return NodeManager.PostJint("debug_traceTransaction", hash, options).Result;
        }

        [CliFunction("debug", "traceTransactionByBlockAndIndex")]
        public JsValue TraceTransactionByBlockAndIndex(string hash, object options)
        {
            return NodeManager.PostJint("debug_traceTransactionByBlockAndIndex", hash, options).Result;
        }

        [CliFunction("debug", "traceTransactionByBlockhashAndIndex")]
        public JsValue TraceTransactionByBlockhashAndIndex(string hash, object options)
        {
            return NodeManager.PostJint("debug_traceTransactionByBlockhashAndIndex", hash, options).Result;
        }

        [CliFunction("debug", "traceTransactionInBlockByHash")]
        public JsValue TraceTransactionInBlockByHash(string rlp, string hash, object options)
        {
            return NodeManager.PostJint("debug_traceTransactionInBlockByHash", rlp, hash, options).Result;
        }

        [CliFunction("debug", "traceTransactionInBlockByIndex")]
        public JsValue TraceTransactionInBlockByIndex(string rlp, int index, object options)
        {
            return NodeManager.PostJint("debug_traceTransactionInBlockByIndex", rlp, index, options).Result;
        }

        [CliFunction("debug", "config")]
        public string? GetConfigValue(string category, string name)
        {
            return NodeManager.Post<string>("debug_getConfigValue", category, name).Result;
        }

        [CliFunction("debug", "getBlockRlpByHash")]
        public string? GetBlockRlpByHash(string hash)
        {
            return NodeManager.Post<string>("debug_getBlockRlpByHash", CliParseHash(hash)).Result;
        }

        [CliFunction("debug", "getBlockRlp")]
        public string? GetBlockRlp(long number)
        {
            return NodeManager.Post<string>("debug_getBlockRlp", number).Result;
        }
        
        [CliFunction("debug", "migrateReceipts")]
        public bool MigrateReceipts(long number)
        {
            return NodeManager.Post<bool>("debug_migrateReceipts", number).Result;
        }

        public DebugCliModule(ICliEngine cliEngine, INodeManager nodeManager) : base(cliEngine, nodeManager)
        {
        }
    }
}
