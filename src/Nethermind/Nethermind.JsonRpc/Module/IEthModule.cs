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
using Nethermind.JsonRpc.DataModel;

namespace Nethermind.JsonRpc.Module
{
    public interface IEthModule : IModule
    {
        ResultWrapper<string> eth_protocolVersion();
        ResultWrapper<SynchingResult> eth_syncing();
        ResultWrapper<Data> eth_coinbase();
        ResultWrapper<bool> eth_mining();
        ResultWrapper<Quantity> eth_hashrate();
        ResultWrapper<Quantity> eth_gasPrice();
        ResultWrapper<IEnumerable<Data>> eth_accounts();
        ResultWrapper<Quantity> eth_blockNumber();
        ResultWrapper<Quantity> eth_getBalance(Data address, BlockParameter blockParameter);
        ResultWrapper<Data> eth_getStorageAt(Data address, Quantity positionIndex, BlockParameter blockParameter);
        ResultWrapper<Quantity> eth_getTransactionCount(Data address, BlockParameter blockParameter);
        ResultWrapper<Quantity> eth_getBlockTransactionCountByHash(Data blockHash);
        ResultWrapper<Quantity> eth_getBlockTransactionCountByNumber(BlockParameter blockParameter);
        ResultWrapper<Quantity> eth_getUncleCountByBlockHash(Data blockHash);
        ResultWrapper<Quantity> eth_getUncleCountByBlockNumber(BlockParameter blockParameter);
        ResultWrapper<Data> eth_getCode(Data address, BlockParameter blockParameter);
        ResultWrapper<Data> eth_sign(Data addressData, Data message);
        ResultWrapper<Data> eth_sendTransaction(Transaction transaction);
        ResultWrapper<Data> eth_sendRawTransaction(Data transation);
        ResultWrapper<Data> eth_call(Transaction transactionCall, BlockParameter blockParameter);
        ResultWrapper<Quantity> eth_estimateGas(Transaction transactionCall, BlockParameter blockParameter);
        ResultWrapper<Block> eth_getBlockByHash(Data blockHash, bool returnFullTransactionObjects);
        ResultWrapper<Block> eth_getBlockByNumber(BlockParameter blockParameter, bool returnFullTransactionObjects);
        ResultWrapper<Transaction> eth_getTransactionByHash(Data transactionHash);
        ResultWrapper<Transaction> eth_getTransactionByBlockHashAndIndex(Data blockHash, Quantity positionIndex);
        ResultWrapper<Transaction> eth_getTransactionByBlockNumberAndIndex(BlockParameter blockParameter, Quantity positionIndex);
        ResultWrapper<TransactionReceipt> eth_getTransactionReceipt(Data txHashData);
        ResultWrapper<Block> eth_getUncleByBlockHashAndIndex(Data blockHashData, Quantity positionIndex);
        ResultWrapper<Block> eth_getUncleByBlockNumberAndIndex(BlockParameter blockParameter, Quantity positionIndex);
        ResultWrapper<IEnumerable<string>> eth_getCompilers();
        ResultWrapper<Data> eth_compileLLL(string code);
        ResultWrapper<Data> eth_compileSolidity(string code);
        ResultWrapper<Data> eth_compileSerpent(string code);
        ResultWrapper<Quantity> eth_newFilter(Filter filter);
        ResultWrapper<Quantity> eth_newBlockFilter();
        ResultWrapper<Quantity> eth_newPendingTransactionFilter(Filter filter);
        ResultWrapper<bool> eth_uninstallFilter(Quantity filterId);
        ResultWrapper<Data[]> eth_getFilterChanges(Quantity filterId);
        ResultWrapper<IEnumerable<Log>> eth_getFilterLogs(Quantity filterId);
        ResultWrapper<IEnumerable<Log>> eth_getLogs(Filter filter);
        ResultWrapper<IEnumerable<Data>> eth_getWork();
        ResultWrapper<bool> eth_submitWork(Data nonce, Data headerPowHash, Data mixDigest);
        ResultWrapper<bool> eth_submitHashrate(string hashRate, string id);
    }
}