// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Numerics;
using Jint.Native;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.JsonRpc.Data;

namespace Nethermind.Cli.Modules
{
    [CliModule("eth")]
    public class EthCliModule : CliModuleBase
    {
        private string? SendEth(Address from, Address address, in UInt256 amountInWei)
        {
            long blockNumber = NodeManager.Post<long>("eth_blockNumber").Result;

            TransactionForRpc tx = new();
            tx.Value = amountInWei;
            tx.Gas = Transaction.BaseTxGasCost;
            tx.GasPrice = (UInt256)Engine.JintEngine.GetValue("gasPrice").AsNumber();
            tx.To = address;
            tx.Nonce = (ulong)NodeManager.Post<long>("eth_getTransactionCount", from, blockNumber).Result;
            tx.From = from;

            Keccak? keccak = NodeManager.Post<Keccak>("eth_sendTransaction", tx).Result;
            return keccak?.Bytes.ToHexString();
        }

        [CliFunction("eth", "syncing")]
        public JsValue Syncing()
        {
            return NodeManager.PostJint("eth_syncing").Result;
        }

        [CliFunction("eth", "getProof")]
        public JsValue GetProof(string address, string[] storageKeys, string? blockParameter = null)
        {
            return NodeManager.PostJint("eth_getProof", CliParseAddress(address), storageKeys.Select(CliParseHash), blockParameter ?? "latest").Result;
        }

        [CliFunction("eth", "call")]
        public string? Call(object tx, string? blockParameter = null)
        {
            return NodeManager.Post<string>("eth_call", tx, blockParameter ?? "latest").Result;
        }


        [CliFunction("eth", "multicallV1")]
        public JsValue MultiCallV1(ulong version, object[] blockCalls, string? blockParameter = null, bool traceTransfers = true)
        {
            return NodeManager.PostJint("eth_multicallV1", 1, blockCalls, blockParameter ?? "latest", traceTransfers).Result;
        }


        [CliFunction("eth", "getBlockByHash")]
        public JsValue GetBlockByHash(string hash, bool returnFullTransactionObjects)
        {
            return NodeManager.PostJint("eth_getBlockByHash", CliParseHash(hash), returnFullTransactionObjects).Result;
        }

        [CliFunction("eth", "getTransactionCount")]
        public string? GetTransactionCount(string address, string? blockParameter = null)
        {
            return NodeManager.Post<string>("eth_getTransactionCount", CliParseAddress(address), blockParameter ?? "latest").Result;
        }

        [CliFunction("eth", "getStorageAt")]
        public string? GetStorageAt(string address, string positionIndex, string? blockParameter = null)
        {
            return NodeManager.Post<string>("eth_getStorageAt", CliParseAddress(address), positionIndex, blockParameter ?? "latest").Result;
        }

        [CliFunction("eth", "getBlockByNumber")]
        public JsValue GetBlockByNumber(string blockParameter, bool returnFullTransactionObjects = false)
        {
            return NodeManager.PostJint("eth_getBlockByNumber", blockParameter, returnFullTransactionObjects).Result;
        }

        [CliFunction("eth", "sendEth")]
        public string? SendEth(string from, string to, decimal amountInEth)
        {
            return SendEth(CliParseAddress(from), CliParseAddress(to), (UInt256)(amountInEth * (decimal)1.Ether()));
        }

        [CliFunction("eth", "estimateGas")]
        public string? EstimateGas(object json, string? blockParameter = null)
        {
            return NodeManager.Post<string>("eth_estimateGas", json, blockParameter ?? "latest").Result;
        }

        [CliFunction("eth", "createAccessList")]
        public JsValue CreateAccessList(object tx, string? blockParameter = null, bool optimize = true)
        {
            if (optimize)
            {
                // to support Geth
                return NodeManager.PostJint("eth_createAccessList", tx, blockParameter ?? "latest").Result;
            }
            else
            {
                return NodeManager.PostJint("eth_createAccessList", tx, blockParameter ?? "latest", optimize).Result;
            }
        }

        [CliFunction("eth", "sendWei")]
        public string? SendWei(string from, string to, BigInteger amountInWei)
        {
            return SendEth(CliParseAddress(from), CliParseAddress(to), (UInt256)amountInWei);
        }

        [CliFunction("eth", "sendRawTransaction")]
        public string? SendRawTransaction(string txRlp)
        {
            return NodeManager.Post<string>("eth_sendRawTransaction", txRlp).Result;
        }

        [CliFunction("eth", "sendTransaction")]
        public string? SendTransaction(object tx)
        {
            return NodeManager.Post<string>("eth_sendTransaction", tx).Result;
        }

        [CliProperty("eth", "blockNumber")]
        public long BlockNumber()
        {
            return NodeManager.Post<long>("eth_blockNumber").Result;
        }

        [CliFunction("eth", "getCode")]
        public string? GetCode(string address, string? blockParameter = null)
        {
            return NodeManager.Post<string>("eth_getCode", CliParseAddress(address), blockParameter ?? "latest").Result;
        }

        [CliFunction("eth", "getBlockTransactionCountByNumber")]
        public long GetBlockTransactionCountByNumber(string? blockParameter)
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

        [CliFunction("eth", "getUncleByBlockNumberAndIndex")]
        public JsValue GetUncleByBlockNumberAndIndex(string blockParameter, int index)
        {
            return NodeManager.PostJint("eth_getUncleByBlockNumberAndIndex", blockParameter, index).Result;
        }

        [CliFunction("eth", "getUncleByBlockHashAndIndex")]
        public JsValue GetUncleByBlockHashAndIndex(string hash, int index)
        {
            return NodeManager.PostJint("eth_getUncleByBlockHashAndIndex", CliParseHash(hash), index).Result;
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
        public BigInteger GetBalance(string address, string? blockParameter = null)
        {
            return NodeManager.Post<BigInteger>("eth_getBalance", CliParseAddress(address), blockParameter ?? "latest").Result;
        }

        [CliProperty("eth", "chainId")]
        public string? ChainId()
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

        [CliFunction("eth", "getFilterChanges")]
        public JsValue GetFilterChanges(long filterId)
        {
            return NodeManager.PostJint("eth_getFilterChanges", filterId).Result;
        }

        [CliFunction("eth", "newPendingTransactionFilter")]
        public long NewPendingTransactionFilter()
        {
            return NodeManager.Post<long>("eth_newPendingTransactionFilter").Result;
        }

        [CliFunction("eth", "feeHistory")]
        public JsValue FeeHistory(long blockCount, string newestBlock, double[]? rewardPercentiles = null)
        {
            return NodeManager.PostJint("eth_feeHistory", blockCount, newestBlock, rewardPercentiles!).Result;
        }

        [CliFunction("eth", "gasPrice")]
        public JsValue GasPrice()
        {
            return NodeManager.PostJint("eth_gasPrice").Result;
        }

        [CliFunction("eth", "maxPriorityFeePerGas")]
        public JsValue MaxPriorityFeePerGas()
        {
            return NodeManager.PostJint("eth_maxPriorityFeePerGas").Result;
        }

        [CliFunction("eth", "getAccount")]
        public JsValue GetAccount(Address accountAddress, string? blockParam = null)
        {
            return NodeManager.PostJint("eth_getAccount", accountAddress, blockParam ?? "latest").Result;
        }

        public EthCliModule(ICliEngine cliEngine, INodeManager nodeManager) : base(cliEngine, nodeManager)
        {
        }
    }
}
