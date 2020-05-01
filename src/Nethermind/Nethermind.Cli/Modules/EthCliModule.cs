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

using System.Linq;
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
            tx.Gas = Transaction.BaseTxGasCost;
            tx.GasPrice = (UInt256) Engine.JintEngine.GetValue("gasPrice").AsNumber();
            tx.To = address;
            tx.Nonce = (ulong) NodeManager.Post<long>("eth_getTransactionCount", from, blockNumber).Result;
            tx.From = from;

            Keccak keccak = NodeManager.Post<Keccak>("eth_sendTransaction", tx).Result;
            return keccak.Bytes.ToHexString();
        }

        [CliFunction("eth", "getProof")]
        public JsValue Call(string address, string[] storageKeys, string blockParameter = null)
        {
            return NodeManager.PostJint("eth_getProof", CliParseAddress(address), storageKeys.Select(CliParseHash), blockParameter ?? "latest").Result;
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
        public JsValue GetBlockByNumber(string blockParameter, bool returnFullTransactionObjects = false)
        {
            return NodeManager.PostJint("eth_getBlockByNumber", blockParameter, returnFullTransactionObjects).Result;
        }

        [CliFunction("eth", "sendEth")]
        public string SendEth(string from, string to, decimal amountInEth)
        {
            return SendEth(CliParseAddress(from), CliParseAddress(to), (UInt256) (amountInEth * (decimal) 1.Ether()));
        }

        [CliFunction("eth", "estimateGas")]
        public string EstimateGas(object json)
        {
            return NodeManager.Post<string>("eth_estimateGas", json).Result;
        }

        [CliFunction("eth", "sendWei")]
        public string SendWei(string from, string to, BigInteger amountInWei)
        {
            return SendEth(CliParseAddress(from), CliParseAddress(to), (UInt256) amountInWei);
        }

        [CliFunction("eth", "sendRawTransaction")]
        public string SendRawTransaction(string txRlp)
        {
            return NodeManager.Post<string>("eth_sendRawTransaction", txRlp).Result;
        }
        
        [CliFunction("eth", "sendTransaction")]
        public string SendTransaction(object tx)
        {
            return NodeManager.Post<string>("eth_sendTransaction", tx).Result;
        }

        [CliProperty("eth", "blockNumber")]
        public long BlockNumber()
        {
            return NodeManager.Post<long>("eth_blockNumber").Result;
        }

        [CliFunction("eth", "getCode")]
        public string GetCode(string address, string blockParameter = null)
        {
            return NodeManager.Post<string>("eth_getCode", CliParseAddress(address), blockParameter ?? "latest").Result;
        }

        [CliFunction("eth", "getBlockTransactionCountByNumber")]
        public long GetBlockTransactionCountByNumber(string blockParameter)
        {
            return NodeManager.Post<long>("eth_getBlockTransactionCountByNumber", blockParameter).Result;
        }

        [CliFunction("eth", "getBlockTransactionCountByHash")]
        public long GetBlockTransactionCountByHash(string hash)
        {
            return NodeManager.Post<long>("eth_getBlockTransactionCountByHash", CliParseHash(hash)).Result;
        }

        [CliFunction("eth", "getUncleCountByBlockNumber")]
        public long GetUncleCountByBlockNumber(string blockParameter)
        {
            return NodeManager.Post<long>("eth_getUncleCountByBlockNumber", blockParameter).Result;
        }

        [CliFunction("eth", "getTransactionByBlockNumberAndIndex")]
        public JsValue GetTransactionByBlockNumberAndIndex(string blockParameter, string index)
        {
            return NodeManager.PostJint("eth_getTransactionByBlockNumberAndIndex", blockParameter, index).Result;
        }
        
        [CliFunction("eth", "getTransactionByHash")]
        public JsValue GetTransactionByHash(string txHash)
        {
            return NodeManager.PostJint("eth_getTransactionByHash", CliParseHash(txHash)).Result;
        }
        
        [CliProperty("eth", "pendingTransactions")]
        public JsValue PendingTransactions()
        {
            return NodeManager.PostJint("eth_pendingTransactions").Result;
        }

        [CliFunction("eth", "getTransactionReceipt")]
        public JsValue GetTransactionReceipt(string txHash)
        {
            return NodeManager.PostJint("eth_getTransactionReceipt", CliParseHash(txHash)).Result;
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
        public JsValue GetLogs(object filter)
        {
            return NodeManager.PostJint("eth_getLogs", filter).Result;
        }

        public EthCliModule(ICliEngine cliEngine, INodeManager nodeManager) : base(cliEngine, nodeManager)
        {
        }
    }
}