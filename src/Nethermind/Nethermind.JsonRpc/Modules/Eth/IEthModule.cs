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

using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Blockchain.Filters;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Dirichlet.Numerics;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Eip1186;

namespace Nethermind.JsonRpc.Modules.Eth
{
    [RpcModule(ModuleType.Eth)]
    public interface IEthModule : IModule
    {
        [JsonRpcMethod(IsImplemented = true, Description = "Returns ChainID", IsReadOnly = true)]
        ResultWrapper<long> eth_chainId();
        
        [JsonRpcMethod(IsImplemented = true, Description = "Returns ETH protocol version", IsReadOnly = true)]
        ResultWrapper<string> eth_protocolVersion();
        
        [JsonRpcMethod(IsImplemented = true, Description = "Returns syncing status", IsReadOnly = true)]
        ResultWrapper<SyncingResult> eth_syncing();
        
        [JsonRpcMethod(IsImplemented = false, Description = "Returns miner's coinbase'", IsReadOnly = true)]
        ResultWrapper<Address> eth_coinbase();
        
        [JsonRpcMethod(IsImplemented = false, Description = "Returns mining status", IsReadOnly = true)]
        ResultWrapper<bool?> eth_mining();
        
        [JsonRpcMethod(IsImplemented = false, Description = "Returns full state snapshot", IsReadOnly = true)]
        ResultWrapper<byte[]> eth_snapshot();
        
        [JsonRpcMethod(IsImplemented = false, Description = "Returns mining hashrate", IsReadOnly = true)]
        ResultWrapper<UInt256?> eth_hashrate();
        
        [JsonRpcMethod(IsImplemented = false, Description = "Returns miner's gas price", IsReadOnly = true)]
        ResultWrapper<UInt256?> eth_gasPrice();
        
        [JsonRpcMethod(IsImplemented = false, Description = "Returns accounts", IsReadOnly = true)]
        ResultWrapper<IEnumerable<Address>> eth_accounts();
        
        [JsonRpcMethod(IsImplemented = true, Description = "Returns current block number", IsReadOnly = true)]
        Task<ResultWrapper<long?>> eth_blockNumber();
        
        [JsonRpcMethod(IsImplemented = true, Description = "Returns account balance", IsReadOnly = true)]
        Task<ResultWrapper<UInt256?>> eth_getBalance(Address address, BlockParameter blockParameter = null);
        
        [JsonRpcMethod(IsImplemented = true, Description = "Returns storage data at address. storage_index", IsReadOnly = true)]
        ResultWrapper<byte[]> eth_getStorageAt(Address address, UInt256 positionIndex, BlockParameter blockParameter = null);
        
        [JsonRpcMethod(IsImplemented = true, Description = "Returns number of transactions in the block", IsReadOnly = true)]
        ResultWrapper<UInt256?> eth_getTransactionCount(Address address, BlockParameter blockParameter = null);
        
        [JsonRpcMethod(IsImplemented = true, Description = "Returns number of transactions in the block block hash", IsReadOnly = true)]
        ResultWrapper<UInt256?> eth_getBlockTransactionCountByHash(Keccak blockHash);
        
        [JsonRpcMethod(IsImplemented = true, Description = "Returns number of transactions in the block by block number", IsReadOnly = true)]
        ResultWrapper<UInt256?> eth_getBlockTransactionCountByNumber(BlockParameter blockParameter);
        
        [JsonRpcMethod(IsImplemented = true, Description = "Returns number of uncles in the block by block hash", IsReadOnly = true)]
        ResultWrapper<UInt256?> eth_getUncleCountByBlockHash(Keccak blockHash);
        
        [JsonRpcMethod(IsImplemented = true, Description = "Returns number of uncles in the block by block number", IsReadOnly = true)]
        ResultWrapper<UInt256?> eth_getUncleCountByBlockNumber(BlockParameter blockParameter);
        
        [JsonRpcMethod(IsImplemented = true, Description = "Returns account code at given address and block", IsReadOnly = true)]
        ResultWrapper<byte[]> eth_getCode(Address address, BlockParameter blockParameter = null);
        
        [JsonRpcMethod(IsImplemented = false, Description = "Signs a transaction", IsReadOnly = true)]
        ResultWrapper<byte[]> eth_sign(Address addressData, byte[] message);
        
        [JsonRpcMethod(IsImplemented = true, Description = "Send a transaction to the tx pool and broadcasting", IsReadOnly = false)]
        ResultWrapper<Keccak> eth_sendTransaction(TransactionForRpc transactionForRpc);
        
        [JsonRpcMethod(IsImplemented = true, Description = "Send a raw transaction to the tx pool and broadcasting", IsReadOnly = false)]
        ResultWrapper<Keccak> eth_sendRawTransaction(byte[] transaction);
        
        [JsonRpcMethod(IsImplemented = true, Description = "Executes a tx call (does not create a transaction)", IsReadOnly = false)]
        ResultWrapper<byte[]> eth_call(TransactionForRpc transactionCall, BlockParameter blockParameter = null);
        
        [JsonRpcMethod(IsImplemented = true, Description = "Executes a tx call and returns gas used (does not create a transaction)", IsReadOnly = false)]
        ResultWrapper<UInt256?> eth_estimateGas(TransactionForRpc transactionCall);
        
        [JsonRpcMethod(IsImplemented = true, Description = "Retrieves a block by hash", IsReadOnly = true)]
        ResultWrapper<BlockForRpc> eth_getBlockByHash(Keccak blockHash, bool returnFullTransactionObjects);
        
        [JsonRpcMethod(IsImplemented = true, Description = "Retrieves a block by number", IsReadOnly = true)]
        ResultWrapper<BlockForRpc> eth_getBlockByNumber(BlockParameter blockParameter, bool returnFullTransactionObjects);
        
        [JsonRpcMethod(IsImplemented = true, Description = "Retrieves a transaction by hash", IsReadOnly = true)]
        ResultWrapper<TransactionForRpc> eth_getTransactionByHash(Keccak transactionHash);
        
        [JsonRpcMethod(IsImplemented = true, Description = "Retrieves a transaction by block hash and index", IsReadOnly = true)]
        ResultWrapper<TransactionForRpc> eth_getTransactionByBlockHashAndIndex(Keccak blockHash, UInt256 positionIndex);
        
        [JsonRpcMethod(IsImplemented = true, Description = "Retrieves a transaction by block number and index", IsReadOnly = true)]
        ResultWrapper<TransactionForRpc> eth_getTransactionByBlockNumberAndIndex(BlockParameter blockParameter, UInt256 positionIndex);
        
        [JsonRpcMethod(IsImplemented = true, Description = "Retrieves a transaction receipt by tx hash", IsReadOnly = true)]
        ResultWrapper<ReceiptForRpc> eth_getTransactionReceipt(Keccak txHashData);
        
        [JsonRpcMethod(IsImplemented = true, Description = "Retrieves an uncle block header by block hash and uncle index", IsReadOnly = true)]
        ResultWrapper<BlockForRpc> eth_getUncleByBlockHashAndIndex(Keccak blockHashData, UInt256 positionIndex);
        
        [JsonRpcMethod(IsImplemented = true, Description = "Retrieves an uncle block header by block number and uncle index", IsReadOnly = true)]
        ResultWrapper<BlockForRpc> eth_getUncleByBlockNumberAndIndex(BlockParameter blockParameter, UInt256 positionIndex);
        
        [JsonRpcMethod(IsImplemented = true, Description = "Creates an update filter", IsReadOnly = false)]
        ResultWrapper<UInt256?> eth_newFilter(Filter filter);
        
        [JsonRpcMethod(IsImplemented = true, Description = "Creates an update filter", IsReadOnly = false)]
        ResultWrapper<UInt256?> eth_newBlockFilter();
        
        [JsonRpcMethod(IsImplemented = true, Description = "Creates an update filter", IsReadOnly = false)]
        ResultWrapper<UInt256?> eth_newPendingTransactionFilter();
        
        [JsonRpcMethod(IsImplemented = true, Description = "Creates an update filter", IsReadOnly = false)]
        ResultWrapper<bool?> eth_uninstallFilter(UInt256 filterId);
        
        [JsonRpcMethod(IsImplemented = true, Description = "Reads filter changes", IsReadOnly = true)]
        ResultWrapper<IEnumerable<object>> eth_getFilterChanges(UInt256 filterId);
        
        [JsonRpcMethod(IsImplemented = true, Description = "Reads filter changes", IsReadOnly = true)]
        ResultWrapper<IEnumerable<FilterLog>> eth_getFilterLogs(UInt256 filterId);
        
        [JsonRpcMethod(IsImplemented = true, Description = "Reads logs", IsReadOnly = true)]
        ResultWrapper<IEnumerable<FilterLog>> eth_getLogs(Filter filter);
        
        [JsonRpcMethod(Description = "", IsImplemented = false, IsReadOnly = true)]
        ResultWrapper<IEnumerable<byte[]>> eth_getWork();
        
        [JsonRpcMethod(Description = "", IsImplemented = false, IsReadOnly = false)]
        ResultWrapper<bool?> eth_submitWork(byte[] nonce, Keccak headerPowHash, byte[] mixDigest);
        
        [JsonRpcMethod(Description = "", IsImplemented = false, IsReadOnly = false)]
        ResultWrapper<bool?> eth_submitHashrate(string hashRate, string id);
        
        [JsonRpcMethod(Description = "https://github.com/ethereum/EIPs/issues/1186", IsImplemented = true, IsReadOnly = true)]
        ResultWrapper<AccountProof> eth_getProof(Address accountAddress, byte[][] hashRate, BlockParameter blockParameter);
    }
}