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
using System.Numerics;
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
        ResultWrapper<string> eth_protocolVersion();
        ResultWrapper<SyncingResult> eth_syncing();
        ResultWrapper<Address> eth_coinbase();
        ResultWrapper<bool?> eth_mining();
        ResultWrapper<byte[]> eth_snapshot();
        ResultWrapper<UInt256?> eth_hashrate();
        ResultWrapper<UInt256?> eth_gasPrice();
        ResultWrapper<IEnumerable<Address>> eth_accounts();
        ResultWrapper<UInt256?> eth_blockNumber();
        ResultWrapper<UInt256?> eth_getBalance(Address address, BlockParameter blockParameter);
        ResultWrapper<byte[]> eth_getStorageAt(Address address, UInt256 positionIndex, BlockParameter blockParameter);
        ResultWrapper<UInt256?> eth_getTransactionCount(Address address, BlockParameter blockParameter);
        ResultWrapper<UInt256?> eth_getBlockTransactionCountByHash(Keccak blockHash);
        ResultWrapper<UInt256?> eth_getBlockTransactionCountByNumber(BlockParameter blockParameter);
        ResultWrapper<UInt256?> eth_getUncleCountByBlockHash(Keccak blockHash);
        ResultWrapper<UInt256?> eth_getUncleCountByBlockNumber(BlockParameter blockParameter);
        ResultWrapper<byte[]> eth_getCode(Address address, BlockParameter blockParameter);
        ResultWrapper<byte[]> eth_sign(Address addressData, byte[] message);
        ResultWrapper<Keccak> eth_sendTransaction(TransactionForRpc transactionForRpc);
        ResultWrapper<Keccak> eth_sendRawTransaction(byte[] transaction);
        ResultWrapper<byte[]> eth_call(TransactionForRpc transactionCall, BlockParameter blockParameter = null);
        ResultWrapper<UInt256?> eth_estimateGas(TransactionForRpc transactionCall);
        ResultWrapper<BlockForRpc> eth_getBlockByHash(Keccak blockHash, bool returnFullTransactionObjects);
        ResultWrapper<BlockForRpc> eth_getBlockByNumber(BlockParameter blockParameter, bool returnFullTransactionObjects);
        ResultWrapper<TransactionForRpc> eth_getTransactionByHash(Keccak transactionHash);
        ResultWrapper<TransactionForRpc> eth_getTransactionByBlockHashAndIndex(Keccak blockHash, UInt256 positionIndex);
        ResultWrapper<TransactionForRpc> eth_getTransactionByBlockNumberAndIndex(BlockParameter blockParameter, UInt256 positionIndex);
        ResultWrapper<ReceiptForRpc> eth_getTransactionReceipt(Keccak txHashData);
        ResultWrapper<BlockForRpc> eth_getUncleByBlockHashAndIndex(Keccak blockHashData, UInt256 positionIndex);
        ResultWrapper<BlockForRpc> eth_getUncleByBlockNumberAndIndex(BlockParameter blockParameter, UInt256 positionIndex);
        ResultWrapper<UInt256?> eth_newFilter(Filter filter);
        ResultWrapper<UInt256?> eth_newBlockFilter();
        ResultWrapper<UInt256?> eth_newPendingTransactionFilter();
        ResultWrapper<bool?> eth_uninstallFilter(UInt256 filterId);
        ResultWrapper<IEnumerable<object>> eth_getFilterChanges(UInt256 filterId);
        ResultWrapper<IEnumerable<FilterLog>> eth_getFilterLogs(UInt256 filterId);
        ResultWrapper<IEnumerable<FilterLog>> eth_getLogs(Filter filter);
        
        [JsonRpcMethod(Description = "", IsImplemented = false)]
        ResultWrapper<IEnumerable<byte[]>> eth_getWork();
        
        [JsonRpcMethod(Description = "", IsImplemented = false)]
        ResultWrapper<bool?> eth_submitWork(byte[] nonce, Keccak headerPowHash, byte[] mixDigest);
        
        [JsonRpcMethod(Description = "", IsImplemented = false)]
        ResultWrapper<bool?> eth_submitHashrate(string hashRate, string id);
        
        // https://github.com/ethereum/EIPs/issues/1186	
        ResultWrapper<AccountProof> eth_getProof(Address accountAddress, byte[][] hashRate, BlockParameter blockParameter);
    }
}