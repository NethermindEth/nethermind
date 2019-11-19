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

using System.Numerics;
using Jint.Native;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Dirichlet.Numerics;
using Nethermind.JsonRpc.Data;

namespace Nethermind.Cli.Modules
{
    [CliModule("eth")]
    public class EthCliModule : CliModuleBase
    {
        private string SendEth(Address from, Address address, UInt256 amountInWei)
        {
            long blockNumber = NodeManager.Post<long>("eth_blockNumber").Result;

            TransactionForRpc tx = new TransactionForRpc();
            tx.Value = amountInWei;
            tx.Gas = 21000;
            tx.GasPrice = (UInt256) Engine.JintEngine.GetValue("gasPrice").AsNumber();
            tx.To = address;
            tx.Nonce = (ulong) NodeManager.Post<long>("eth_getTransactionCount", address, blockNumber).Result;
            tx.From = from;

            Keccak keccak = NodeManager.Post<Keccak>("eth_sendTransaction", tx).Result;
            return keccak.Bytes.ToHexString();
        }

        [CliFunction("eth", "call")]
        public string Call(object tx, string blockParameter = null)
        {
            return NodeManager.Post<string>("eth_call", tx, blockParameter ?? "latest").Result;
        }
        
        [CliFunction("eth", "getBlockByHash")]
        public JsValue GetBlockByHash(string hash, bool returnFullTransactionObjects)
        {
            return NodeManager.PostJint("eth_getBlockByHash", CliParseHash(hash), returnFullTransactionObjects).Result;
        }

        [CliFunction("eth", "getTransactionCount")]
        public string GetTransactionCount(string address, string blockParameter = null)
        {
            return NodeManager.Post<string>("eth_getTransactionCount", CliParseAddress(address), blockParameter ?? "latest").Result;
        }

        [CliFunction("eth", "getStorageAt")]
        public string GetStorageAt(string address, string positionIndex, string blockParameter = null)
        {
            return NodeManager.Post<string>("eth_getStorageAt", CliParseAddress(address), positionIndex, blockParameter ?? "latest").Result;
        }

        [CliFunction("eth", "getBlockByNumber")]
        public JsValue GetBlockByNumber(string blockParameter, bool returnFullTransactionObjects)
        {
            return NodeManager.PostJint("eth_getBlockByNumber", blockParameter, returnFullTransactionObjects).Result;
        }

        [CliFunction("eth", "sendEth")]
        public string SendEth(string from, string to, decimal amountInEth)
        {
            return SendEth(CliParseAddress(from), CliParseAddress(to), (UInt256) (amountInEth * (decimal) 1.Ether()));
        }

        [CliFunction("eth", "sendWei")]
        public string SendWei(string from, string to, BigInteger amountInWei)
        {
            return SendEth(CliParseAddress(from), CliParseAddress(to), (UInt256) amountInWei);
        }

        [CliFunction("eth", "sendRawTransaction")]
        public string SendWei(string txRlp)
        {
            return NodeManager.Post<string>("eth_sendRawTransaction", txRlp).Result;
        }

        [CliProperty("eth", "blockNumber")]
        public long BlockNumber()
        {
            return NodeManager.Post<long>("eth_blockNumber").Result;
        }

        [CliFunction("eth", "getCode")]
        public string GetCode(string address, string blockParameter = null)
        {
            return NodeManager.Post<string>("eth_getCode", address, blockParameter ?? "latest").Result;
        }

        [CliFunction("eth", "getBlockTransactionCountByNumber")]
        public JsValue GetBlockTransactionCountByNumber(string blockParameter)
        {
            return NodeManager.PostJint("eth_getBlockTransactionCountByNumber", blockParameter).Result;
        }

        [CliFunction("eth", "getBlockTransactionCountByHash")]
        public JsValue GetBlockTransactionCountByHash(string hash)
        {
            return NodeManager.PostJint("eth_getBlockTransactionCountByHash", hash).Result;
        }

        [CliFunction("eth", "getUncleCountByBlockNumber")]
        public JsValue GetUncleCountByBlockNumber(string blockParameter)
        {
            return NodeManager.PostJint("eth_getUncleCountByBlockNumber", blockParameter).Result;
        }

        [CliFunction("eth", "getTransactionByBlockNumberAndIndex")]
        public JsValue GetTransactionByBlockNumberAndIndex(string blockParameter, string index)
        {
            return NodeManager.PostJint("eth_getTransactionByBlockNumberAndIndex", blockParameter, index).Result;
        }

        [CliFunction("eth", "getTransactionReceipt")]
        public JsValue GetTransactionReceipt(string txHash)
        {
            return NodeManager.PostJint("eth_getTransactionReceipt", txHash).Result;
        }

        [CliFunction("eth", "getBalance")]
        public BigInteger GetBalance(string address, string blockParameter = null)
        {
            return NodeManager.Post<BigInteger>("eth_getBalance", CliParseAddress(address), blockParameter ?? "latest").Result;
        }

        [CliProperty("eth", "chainId")]
        public string ChainId()
        {
            return NodeManager.Post<string>("eth_chainId").Result;
        }

        [CliProperty("eth", "protocolVersion")]
        public JsValue ProtocolVersion()
        {
            return NodeManager.PostJint("eth_protocolVersion").Result;
        }

        [CliFunction("eth", "getLogs")]
        public JsValue GetLogs(object json)
        {
            return NodeManager.PostJint("eth_getLogs", json).Result;
        }

        public EthCliModule(ICliEngine cliEngine, INodeManager nodeManager) : base(cliEngine, nodeManager)
        {
        }
    }
}