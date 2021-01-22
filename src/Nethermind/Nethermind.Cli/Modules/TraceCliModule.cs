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
    [CliModule("trace")]
    public class TraceCliModule : CliModuleBase
    {
        [CliFunction("trace", "replayTransaction", Description = "Replays a transaction, returning the traces.")]
        public JsValue ReplayTransaction(string txHash, string[] traceTypes)
        {
            return NodeManager.PostJint("trace_replayTransaction", CliParseHash(txHash), traceTypes).Result;
        }
        
        [CliFunction("trace", "transaction", Description = "Returns all traces of given transaction")]
        public JsValue TraceTransaction(string txHash)
        {
            return NodeManager.PostJint("trace_transaction", CliParseHash(txHash)).Result;
        }
        
        [CliFunction("trace", "replayBlockTransactions", Description = "Replays all transactions in a block returning the requested traces for each transaction.")]
        public JsValue ReplayBlockTransactions(string blockNumber, string[] traceTypes)
        {
            return NodeManager.PostJint("trace_replayBlockTransactions", blockNumber, traceTypes).Result;
        }
        
        [CliFunction("trace", "block", Description = "Returns traces created at given block.")]
        public JsValue TraceBlock(string blockNumber)
        {
            return NodeManager.PostJint("trace_block", blockNumber).Result;
        }
        
        [CliFunction("trace", "rawTransaction", Description = "Traces a call to eth_sendRawTransaction without making the call, returning the traces")]
        public JsValue TraceRawTransaction(string txData, string[] traceTypes)
        {
            return NodeManager.PostJint("trace_rawTransaction", txData, traceTypes).Result;
        }
        
        [CliFunction("trace", "call", Description = "Traces a call, returning the traces")]
        public JsValue TraceCall(object transaction, string[] traceTypes, string blockNumber)
        {
            return NodeManager.PostJint("trace_call", transaction, traceTypes, blockNumber).Result;
        }

        public TraceCliModule(ICliEngine cliEngine, INodeManager nodeManager) : base(cliEngine, nodeManager)
        {
        }
    }
}
