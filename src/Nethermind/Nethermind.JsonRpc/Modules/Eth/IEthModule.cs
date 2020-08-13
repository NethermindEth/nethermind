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

using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Dirichlet.Numerics;
using Nethermind.JsonRpc.Data;
using Nethermind.State.Proofs;

namespace Nethermind.JsonRpc.Modules.Eth
{
    [RpcModule(ModuleType.Eth)]
    public interface IEthModule : IModule
    {
        [JsonRpcMethod(IsImplemented = true, Description = "Returns ChainID", Returns = "Quantity", IsReadOnly = true)]
        ResultWrapper<long> eth_chainId();
        
        [JsonRpcMethod(IsImplemented = true, Description = "Returns ETH protocol version", Returns = "String", IsReadOnly = true)]
        ResultWrapper<string> eth_protocolVersion();
        
        [JsonRpcMethod(IsImplemented = true, Description = "Returns syncing status", Returns = "Object", IsReadOnly = true)]
        ResultWrapper<SyncingResult> eth_syncing();
        
        [JsonRpcMethod(IsImplemented = false, Description = "Returns miner's coinbase", Returns = "Address", IsReadOnly = true)]
        ResultWrapper<Address> eth_coinbase();
        
        [JsonRpcMethod(IsImplemented = false, Description = "Returns mining status", Returns = "Boolean", IsReadOnly = true)]
        ResultWrapper<bool?> eth_mining();
        
        [JsonRpcMethod(IsImplemented = false, Description = "Returns full state snapshot", Returns = "Data", IsReadOnly = true)]
        ResultWrapper<byte[]> eth_snapshot();
        
        [JsonRpcMethod(IsImplemented = false, Description = "Returns mining hashrate", Returns = "Quantity", IsReadOnly = true)]
        ResultWrapper<UInt256?> eth_hashrate();
        
        [JsonRpcMethod(IsImplemented = false, Description = "Returns miner's gas price", Returns = "Quantity", IsReadOnly = true)]
        ResultWrapper<UInt256?> eth_gasPrice();
        
        [JsonRpcMethod(IsImplemented = false, Description = "Returns accounts", Returns = "Array", IsReadOnly = true)]
        ResultWrapper<IEnumerable<Address>> eth_accounts();
        
        [JsonRpcMethod(IsImplemented = true, Description = "Returns current block number", Returns = "Quantity", IsReadOnly = true)]
        Task<ResultWrapper<long?>> eth_blockNumber();
        
        [JsonRpcMethod(IsImplemented = true, Description = "Returns account balance", Returns = "Quantity", IsReadOnly = true)]
        Task<ResultWrapper<UInt256?>> eth_getBalance(Address address, BlockParameter blockParameter = null);
        
        [JsonRpcMethod(IsImplemented = true, Description = "Returns storage data at address. storage_index", Returns = "Data", IsReadOnly = true)]
        ResultWrapper<byte[]> eth_getStorageAt(Address address, UInt256 positionIndex, BlockParameter blockParameter = null);
        
        [JsonRpcMethod(IsImplemented = true, Description = "Returns account nonce (number of trnsactions from the account since genesis) at the given block number", Returns = "Quantity", IsReadOnly = true)]
        Task<ResultWrapper<UInt256?>> eth_getTransactionCount(Address address, BlockParameter blockParameter = null);
        
        [JsonRpcMethod(IsImplemented = true, Description = "Returns number of transactions in the block block hash", Returns = "Quantity", IsReadOnly = true)]
        ResultWrapper<UInt256?> eth_getBlockTransactionCountByHash(Keccak blockHash);
        
        [JsonRpcMethod(IsImplemented = true, Description = "Returns number of transactions in the block by block number", Returns = "Quantity", IsReadOnly = true)]
        ResultWrapper<UInt256?> eth_getBlockTransactionCountByNumber(BlockParameter blockParameter);
        
        [JsonRpcMethod(IsImplemented = true, Description = "Returns number of uncles in the block by block hash", Returns = "Quantity", IsReadOnly = true)]
        ResultWrapper<UInt256?> eth_getUncleCountByBlockHash(Keccak blockHash);
        
        [JsonRpcMethod(IsImplemented = true, Description = "Returns number of uncles in the block by block number", Returns = "Quantity", IsReadOnly = true)]
        ResultWrapper<UInt256?> eth_getUncleCountByBlockNumber(BlockParameter blockParameter);
        
        [JsonRpcMethod(IsImplemented = true, Description = "Returns account code at given address and block", Returns = "Data", IsReadOnly = true)]
        ResultWrapper<byte[]> eth_getCode(Address address, BlockParameter blockParameter = null);
        
        [JsonRpcMethod(IsImplemented = false, Description = "Signs a transaction", Returns = "Data", IsReadOnly = true)]
        ResultWrapper<byte[]> eth_sign(Address addressData, byte[] message);
        
        [JsonRpcMethod(IsImplemented = true, Description = "Send a transaction to the tx pool and broadcasting", Returns = "Data", IsReadOnly = true)]
        Task<ResultWrapper<Keccak>> eth_sendTransaction(TransactionForRpc transactionForRpc);
        
        [JsonRpcMethod(IsImplemented = true, Description = "Send a raw transaction to the tx pool and broadcasting", Returns = "Data", IsReadOnly = true)]
        Task<ResultWrapper<Keccak>> eth_sendRawTransaction(byte[] transaction);
        
        [JsonRpcMethod(IsImplemented = true, Description = "Executes a tx call (does not create a transaction)", Returns = "String", IsReadOnly = false)]
        ResultWrapper<string> eth_call(TransactionForRpc transactionCall, BlockParameter blockParameter = null);
        
        [JsonRpcMethod(IsImplemented = true, Description = "Executes a tx call and returns gas used (does not create a transaction)", Returns = "Quantity", IsReadOnly = false)]
        ResultWrapper<UInt256?> eth_estimateGas(TransactionForRpc transactionCall, BlockParameter blockParameter = null);
        
        [JsonRpcMethod(IsImplemented = true, Description = "Retrieves a block by hash", Returns = "Quantity", IsReadOnly = true)]
        ResultWrapper<BlockForRpc> eth_getBlockByHash(Keccak blockHash, bool returnFullTransactionObjects = false);
        
        [JsonRpcMethod(IsImplemented = true, Description = "Retrieves a block by number", Returns = "Object", IsReadOnly = true)]
        ResultWrapper<BlockForRpc> eth_getBlockByNumber(BlockParameter blockParameter, bool returnFullTransactionObjects = false);
        
        [JsonRpcMethod(IsImplemented = true, Description = "Retrieves a transaction by hash", Returns = "Object", IsReadOnly = true)]
        ResultWrapper<TransactionForRpc> eth_getTransactionByHash(Keccak transactionHash);
        
        [JsonRpcMethod(IsImplemented = true, Description = "Returns the pending transactions list", Returns = "Array", IsReadOnly = true)]
        ResultWrapper<TransactionForRpc[]> eth_pendingTransactions();
        
        [JsonRpcMethod(IsImplemented = true, Description = "Retrieves a transaction by block hash and index", Returns = "Object", IsReadOnly = true)]
        ResultWrapper<TransactionForRpc> eth_getTransactionByBlockHashAndIndex(Keccak blockHash, UInt256 positionIndex);
        
        [JsonRpcMethod(IsImplemented = true, Description = "Retrieves a transaction by block number and index", Returns = "Object", IsReadOnly = true)]
        ResultWrapper<TransactionForRpc> eth_getTransactionByBlockNumberAndIndex(BlockParameter blockParameter, UInt256 positionIndex);
        
        [JsonRpcMethod(IsImplemented = true, Description = "Retrieves a transaction receipt by tx hash", Returns = "Object", IsReadOnly = true)]
        Task<ResultWrapper<ReceiptForRpc>> eth_getTransactionReceipt(Keccak txHashData);
        
        [JsonRpcMethod(IsImplemented = true, Description = "Retrieves an uncle block header by block hash and uncle index", Returns = "Object", IsReadOnly = true)]
        ResultWrapper<BlockForRpc> eth_getUncleByBlockHashAndIndex(Keccak blockHashData, UInt256 positionIndex);
        
        [JsonRpcMethod(IsImplemented = true, Description = "Retrieves an uncle block header by block number and uncle index", Returns = "Object", IsReadOnly = true)]
        ResultWrapper<BlockForRpc> eth_getUncleByBlockNumberAndIndex(BlockParameter blockParameter, UInt256 positionIndex);
        
        [JsonRpcMethod(IsImplemented = true, Description = "Creates an update filter", Returns = "Quantity", IsReadOnly = false)]
        ResultWrapper<UInt256?> eth_newFilter(Filter filter);
        
        [JsonRpcMethod(IsImplemented = true, Description = "Creates an update filter", Returns = "Quantity", IsReadOnly = false)]
        ResultWrapper<UInt256?> eth_newBlockFilter();
        
        [JsonRpcMethod(IsImplemented = true, Description = "Creates an update filter", Returns = "Quantity", IsReadOnly = false)]
        ResultWrapper<UInt256?> eth_newPendingTransactionFilter();
        
        [JsonRpcMethod(IsImplemented = true, Description = "Creates an update filter", Returns = "Boolean", IsReadOnly = false)]
        ResultWrapper<bool?> eth_uninstallFilter(UInt256 filterId);
        
        [JsonRpcMethod(IsImplemented = true, Description = "Reads filter changes", Returns = "Array", IsReadOnly = true)]
        ResultWrapper<IEnumerable<object>> eth_getFilterChanges(UInt256 filterId);
        
        [JsonRpcMethod(IsImplemented = true, Description = "Reads filter changes", Returns = "Array", IsReadOnly = true)]
        ResultWrapper<IEnumerable<FilterLog>> eth_getFilterLogs(UInt256 filterId);
        
        [JsonRpcMethod(IsImplemented = true, Description = "Reads logs", Returns = "Array", IsReadOnly = true)]
        ResultWrapper<IEnumerable<FilterLog>> eth_getLogs(Filter filter);
        
        [JsonRpcMethod(Description = "", IsImplemented = false, IsReadOnly = true)]
        ResultWrapper<IEnumerable<byte[]>> eth_getWork();
        
        [JsonRpcMethod(Description = "", IsImplemented = false, IsReadOnly = false)]
        ResultWrapper<bool?> eth_submitWork(byte[] nonce, Keccak headerPowHash, byte[] mixDigest);
        
        [JsonRpcMethod(Description = "", IsImplemented = false, IsReadOnly = false)]
        ResultWrapper<bool?> eth_submitHashrate(string hashRate, string id);
        
        [JsonRpcMethod(Description = "https://github.com/ethereum/EIPs/issues/1186", IsImplemented = true, Returns = "Object", IsReadOnly = true)]
        ResultWrapper<AccountProof> eth_getProof(Address accountAddress, byte[][] hashRate, BlockParameter blockParameter);
    }
}