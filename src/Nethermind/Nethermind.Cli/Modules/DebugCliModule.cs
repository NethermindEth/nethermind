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

using Nethermind.JsonRpc.Modules.DebugModule;

namespace Nethermind.Cli.Modules
{
    [CliModule("debug")]
    public class DebugCliModule : CliModuleBase
    {
        [CliFunction("debug", "traceBlock")]
        public object TraceBlock(string hash)
        {
            return NodeManager.Post<object>("debug_traceBlock", hash).Result;
        }

        [CliFunction("debug", "traceBlockByNumber")]
        public object TraceBlockByNumber(string number)
        {
            return NodeManager.Post<object>("debug_traceBlockByNumber", number).Result;
        }

        [CliFunction("debug", "traceBlockByHash")]
        public object TraceBlockByHash(string hash)
        {
            return NodeManager.Post<object>("debug_traceBlockByHash", hash).Result;
        }

        [CliFunction("debug", "traceTransaction")]
        public object TraceTransaction(string hash)
        {
            return NodeManager.Post<object>("debug_traceTransaction", hash, new TraceOptions()).Result;
        }
        
        [CliFunction("debug", "traceTransactionByBlockAndIndex")]
        public object TraceTransactionByBlockAndIndex(string hash)
        {
            return NodeManager.Post<object>("debug_traceTransactionByBlockAndIndex", hash).Result;
        }

        [CliFunction("debug", "traceTransactionByBlockhashAndIndex")]
        public object TraceTransactionByBlockhashAndIndex(string hash)
        {
            return NodeManager.Post<object>("debug_traceTransactionByBlockhashAndIndex", hash).Result;
        }

        [CliFunction("debug", "config")]
        public string GetConfigValue(string category, string name)
        {
            return NodeManager.Post<string>("debug_getConfigValue", category, name).Result;
        }

        public DebugCliModule(ICliEngine cliEngine, INodeManager nodeManager) : base(cliEngine, nodeManager)
        {
        }
    }
}