// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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

        [CliFunction("trace", "filter", Description = "Return all traces of the given filter")]
        public JsValue TraceFilter(object traceFilter)
        {
            return NodeManager.PostJint("trace_filter", traceFilter).Result;
        }

        public TraceCliModule(ICliEngine cliEngine, INodeManager nodeManager) : base(cliEngine, nodeManager)
        {
        }
    }
}
