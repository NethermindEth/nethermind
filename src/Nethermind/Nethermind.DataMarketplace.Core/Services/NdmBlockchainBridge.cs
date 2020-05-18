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
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Facade;
using Nethermind.TxPool;

namespace Nethermind.DataMarketplace.Core.Services
{
    public class NdmBlockchainBridge : INdmBlockchainBridge
    {
        private readonly ITxPoolBridge _txPoolBridge;
        private readonly IBlockchainBridge _blockchainBridge;
        private readonly ITxPool _txPool;

        public NdmBlockchainBridge(ITxPoolBridge txPoolBridge, IBlockchainBridge blockchainBridge, ITxPool txPool)
        {
            _txPoolBridge = txPoolBridge ?? throw new ArgumentNullException(nameof(txPoolBridge));
            _blockchainBridge = blockchainBridge ?? throw new ArgumentNullException(nameof(blockchainBridge));
            _txPool = txPool ?? throw new ArgumentNullException(nameof(txPool));
        }

        public Task<long> GetLatestBlockNumberAsync()
        {
            var head = _blockchainBridge.Head;

            return head is null ? Task.FromResult(0L) : Task.FromResult(head.Number);
        }

        public Task<byte[]> GetCodeAsync(Address address) => Task.FromResult(_blockchainBridge.GetCode(address));

        public Task<Block?> FindBlockAsync(Keccak blockHash) => Task.FromResult<Block?>(_blockchainBridge.FindBlock(blockHash));

        public Task<Block?> FindBlockAsync(long blockNumber) =>
            Task.FromResult<Block?>(_blockchainBridge.FindBlock(blockNumber));

        public Task<Block?> GetLatestBlockAsync()
        {
            Block head = _blockchainBridge.Head;
            return head is null
                ? Task.FromResult<Block?>(null)
                : Task.FromResult<Block?>(_blockchainBridge.FindBlock(head.Hash));
        }

        public Task<UInt256> GetNonceAsync(Address address) => Task.FromResult(_blockchainBridge.GetNonce(address));

        public Task<UInt256> ReserveOwnTransactionNonceAsync(Address address)
            => Task.FromResult(_txPool.ReserveOwnTransactionNonce(address));

        public Task<NdmTransaction?> GetTransactionAsync(Keccak transactionHash)
        {
            (TxReceipt receipt, Transaction transaction) = _blockchainBridge.GetTransaction(transactionHash);
            if (transaction is null)
            {
                return Task.FromResult<NdmTransaction?>(null);
            }

            var isPending = receipt is null;

            return Task.FromResult<NdmTransaction?>(new NdmTransaction(transaction, isPending, receipt?.BlockNumber ?? 0,
                receipt?.BlockHash, receipt?.GasUsed ?? 0));
        }

        public Task<int> GetNetworkIdAsync() => Task.FromResult(_blockchainBridge.GetNetworkId());

        public Task<byte[]> CallAsync(Transaction transaction)
        {
            var callOutput = _blockchainBridge.Call(_blockchainBridge.Head?.Header, transaction);
            return Task.FromResult(callOutput.OutputData ?? new byte[] {0});
        }

        public Task<byte[]> CallAsync(Transaction transaction, long blockNumber)
        {
            var block = _blockchainBridge.FindBlock(blockNumber);
            if (block is null)
            {
                return Task.FromResult(Array.Empty<byte>());
            }

            var callOutput = _blockchainBridge.Call(block.Header, transaction);

            return Task.FromResult(callOutput.OutputData ?? new byte[] {0});
        }

        public Task<Keccak?> SendOwnTransactionAsync(Transaction transaction)
            => Task.FromResult<Keccak?>(_txPoolBridge.SendTransaction(transaction, TxHandlingOptions.ManagedNonce | TxHandlingOptions.PersistentBroadcast));
    }
}