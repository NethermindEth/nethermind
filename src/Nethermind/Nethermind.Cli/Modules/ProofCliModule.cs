// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Jint.Native;

namespace Nethermind.Cli.Modules
{
    [CliModule("proof")]
    public class ProofCliModule : CliModuleBase
    {
        [CliFunction("proof", "call")]
        public JsValue Call(object tx, string? blockParameter = null)
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
