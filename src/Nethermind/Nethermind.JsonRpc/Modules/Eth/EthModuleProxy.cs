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

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Blockchain.Filters;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Facade.Proxy;
using Nethermind.Facade.Proxy.Models;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Eip1186;

namespace Nethermind.JsonRpc.Modules.Eth
{
    public class EthModuleProxy : IEthModule
    {
        private readonly IEthJsonRpcClientProxy _proxy;

        public EthModuleProxy(IEthJsonRpcClientProxy proxy)
        {
            _proxy = proxy;
        }

        public ResultWrapper<long> eth_chainId()
        {
            throw new NotSupportedException();
        }

        public ResultWrapper<string> eth_protocolVersion()
        {
            throw new NotSupportedException();
        }

        public ResultWrapper<SyncingResult> eth_syncing()
        {
            throw new NotSupportedException();
        }

        public ResultWrapper<Address> eth_coinbase()
        {
            throw new NotSupportedException();
        }

        public ResultWrapper<bool?> eth_mining()
        {
            throw new NotSupportedException();
        }

        public ResultWrapper<byte[]> eth_snapshot()
        {
            throw new NotSupportedException();
        }

        public ResultWrapper<UInt256?> eth_hashrate()
        {
            throw new NotSupportedException();
        }

        public ResultWrapper<UInt256?> eth_gasPrice()
        {
            throw new NotSupportedException();
        }

        public ResultWrapper<IEnumerable<Address>> eth_accounts()
        {
            throw new NotSupportedException();
        }

        public async Task<ResultWrapper<long?>> eth_blockNumber()
            => ResultWrapper<long?>.From(await _proxy.eth_blockNumber());

        public async Task<ResultWrapper<UInt256?>> eth_getBalance(Address address, BlockParameter blockParameter)
            => ResultWrapper<UInt256?>.From(await _proxy.eth_getBalance(address, MapBlockParameter(blockParameter)));

        public ResultWrapper<byte[]> eth_getStorageAt(Address address, UInt256 positionIndex,
            BlockParameter blockParameter)
        {
            throw new NotSupportedException();
        }

        public ResultWrapper<UInt256?> eth_getTransactionCount(Address address, BlockParameter blockParameter)
        {
            throw new NotSupportedException();
        }

        public ResultWrapper<UInt256?> eth_getBlockTransactionCountByHash(Keccak blockHash)
        {
            throw new NotSupportedException();
        }

        public ResultWrapper<UInt256?> eth_getBlockTransactionCountByNumber(BlockParameter blockParameter)
        {
            throw new NotSupportedException();
        }

        public ResultWrapper<UInt256?> eth_getUncleCountByBlockHash(Keccak blockHash)
        {
            throw new NotSupportedException();
        }

        public ResultWrapper<UInt256?> eth_getUncleCountByBlockNumber(BlockParameter blockParameter)
        {
            throw new NotSupportedException();
        }

        public ResultWrapper<byte[]> eth_getCode(Address address, BlockParameter blockParameter)
        {
            throw new NotSupportedException();
        }

        public ResultWrapper<byte[]> eth_sign(Address addressData, byte[] message)
        {
            throw new NotSupportedException();
        }

        public ResultWrapper<Keccak> eth_sendTransaction(TransactionForRpc transactionForRpc)
        {
            throw new NotSupportedException();
        }

        public ResultWrapper<Keccak> eth_sendRawTransaction(byte[] transaction)
        {
            throw new NotSupportedException();
        }

        public ResultWrapper<byte[]> eth_call(TransactionForRpc transactionCall, BlockParameter blockParameter = null)
        {
            throw new NotSupportedException();
        }

        public ResultWrapper<UInt256?> eth_estimateGas(TransactionForRpc transactionCall)
        {
            throw new NotSupportedException();
        }

        public ResultWrapper<BlockForRpc> eth_getBlockByHash(Keccak blockHash, bool returnFullTransactionObjects)
        {
            throw new NotSupportedException();
        }

        public ResultWrapper<BlockForRpc> eth_getBlockByNumber(BlockParameter blockParameter,
            bool returnFullTransactionObjects)
        {
            throw new NotSupportedException();
        }

        public ResultWrapper<TransactionForRpc> eth_getTransactionByHash(Keccak transactionHash)
        {
            throw new NotSupportedException();
        }

        public ResultWrapper<TransactionForRpc> eth_getTransactionByBlockHashAndIndex(Keccak blockHash,
            UInt256 positionIndex)
        {
            throw new NotSupportedException();
        }

        public ResultWrapper<TransactionForRpc> eth_getTransactionByBlockNumberAndIndex(BlockParameter blockParameter,
            UInt256 positionIndex)
        {
            throw new NotSupportedException();
        }

        public ResultWrapper<ReceiptForRpc> eth_getTransactionReceipt(Keccak txHashData)
        {
            throw new NotSupportedException();
        }

        public ResultWrapper<BlockForRpc> eth_getUncleByBlockHashAndIndex(Keccak blockHashData, UInt256 positionIndex)
        {
            throw new NotSupportedException();
        }

        public ResultWrapper<BlockForRpc> eth_getUncleByBlockNumberAndIndex(BlockParameter blockParameter,
            UInt256 positionIndex)
        {
            throw new NotSupportedException();
        }

        public ResultWrapper<UInt256?> eth_newFilter(Filter filter)
        {
            throw new NotSupportedException();
        }

        public ResultWrapper<UInt256?> eth_newBlockFilter()
        {
            throw new NotSupportedException();
        }

        public ResultWrapper<UInt256?> eth_newPendingTransactionFilter()
        {
            throw new NotSupportedException();
        }

        public ResultWrapper<bool?> eth_uninstallFilter(UInt256 filterId)
        {
            throw new NotSupportedException();
        }

        public ResultWrapper<IEnumerable<object>> eth_getFilterChanges(UInt256 filterId)
        {
            throw new NotSupportedException();
        }

        public ResultWrapper<IEnumerable<FilterLog>> eth_getFilterLogs(UInt256 filterId)
        {
            throw new NotSupportedException();
        }

        public ResultWrapper<IEnumerable<FilterLog>> eth_getLogs(Filter filter)
        {
            throw new NotSupportedException();
        }

        public ResultWrapper<IEnumerable<byte[]>> eth_getWork()
        {
            throw new NotSupportedException();
        }

        public ResultWrapper<bool?> eth_submitWork(byte[] nonce, Keccak headerPowHash, byte[] mixDigest)
        {
            throw new NotSupportedException();
        }

        public ResultWrapper<bool?> eth_submitHashrate(string hashRate, string id)
        {
            throw new NotSupportedException();
        }

        public ResultWrapper<AccountProof> eth_getProof(Address accountAddress, byte[][] hashRate,
            BlockParameter blockParameter)
        {
            throw new NotSupportedException();
        }

        private static BlockParameterModel MapBlockParameter(BlockParameter blockParameter)
        {
            if (blockParameter is null)
            {
                return null;
            }

            if (blockParameter.Type == BlockParameterType.BlockNumber && blockParameter.BlockNumber.HasValue)
            {
                return BlockParameterModel.FromNumber(blockParameter.BlockNumber.Value);
            }

            return new BlockParameterModel
            {
                Type = blockParameter.Type.ToString()
            };
        }
    }
}